// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azd;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Environment = System.Environment;

namespace TypeSpec.Azd.Generator.Tests;

public class AzdClientTests
{
    private const string AccessToken = "test-extension-token";
    private WebApplication _server = null!;
    private string _address = string.Empty;
    private RpcState _state = null!;

    [SetUp]
    public async Task SetUp()
    {
        _state = new RpcState();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_state);
        _server = builder.Build();
        _server.MapGrpcService<TestProjectService>();
        _server.MapGrpcService<TestEventService>();
        await _server.StartAsync();
        _address = _server.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    [Test]
    public async Task CallsTheVersionedHostServiceWithAuthentication()
    {
        using var client = new AzdClient(_address, AccessToken);
        var response = await client.Project.GetAsync(new EmptyRequest()).ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Project.Name, Is.EqualTo("test-project"));
            Assert.That(_state.Authorization, Is.EqualTo(AccessToken));
            Assert.That(_state.Method, Is.EqualTo("/azd.extensions.v1.ProjectService/Get"));
        }
    }

    [Test]
    public async Task ReadsConnectionDetailsFromTheAzdInvocationEnvironment()
    {
        var address = Environment.GetEnvironmentVariable("AZD_SERVER");
        var token = Environment.GetEnvironmentVariable("AZD_ACCESS_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AZD_SERVER", _address);
            Environment.SetEnvironmentVariable("AZD_ACCESS_TOKEN", AccessToken);
            using var client = new AzdClient();
            var response = await client.Project.GetAsync(new EmptyRequest()).ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(response.Project.Name, Is.EqualTo("test-project"));
            Assert.That(_state.Authorization, Is.EqualTo(AccessToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZD_SERVER", address);
            Environment.SetEnvironmentVariable("AZD_ACCESS_TOKEN", token);
        }
    }

    [Test]
    public async Task SubscribesAndReportsLifecycleStatusOverAnAuthenticatedDuplexStream()
    {
        using var client = new AzdClient(_address, AccessToken);
        await using var events = new EventManager(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? projectName = null;
        await events.AddProjectEventHandlerAsync("preprovision", args =>
        {
            projectName = args.Project.Name;
            return Task.CompletedTask;
        }, cancellation.Token);
        await events.ReceiveAsync(cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projectName, Is.EqualTo("test-project"));
            Assert.That(_state.HandlerStatus, Is.EqualTo("completed"));
            Assert.That(_state.Authorization, Is.EqualTo(AccessToken));
            Assert.That(_state.Method, Is.EqualTo("/azd.extensions.v1.EventService/EventStream"));
        }
    }

    [TestCase("", AccessToken)]
    [TestCase("http://localhost:12345", "")]
    public void RejectsMissingHostConnectionDetails(string address, string token)
    {
        Assert.Throws<ArgumentNullException>(() => _ = new AzdClient(address, token));
    }

    public sealed class RpcState
    {
        public string? Authorization { get; set; }
        public string? Method { get; set; }
        public string? HandlerStatus { get; set; }

        public void Authenticate(ServerCallContext context)
        {
            Authorization = context.RequestHeaders.GetValue("authorization");
            Method = context.Method;
            if (Authorization != AccessToken)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing extension access token."));
            }
        }
    }

    public sealed class TestProjectService(RpcState state) : ProjectService.ProjectServiceBase
    {
        public override Task<GetProjectResponse> Get(EmptyRequest request, ServerCallContext context)
        {
            state.Authenticate(context);
            return Task.FromResult(new GetProjectResponse { Project = new ProjectConfig { Name = "test-project" } });
        }
    }

    public sealed class TestEventService(RpcState state) : EventService.EventServiceBase
    {
        public override async Task EventStream(IAsyncStreamReader<EventMessage> requests,
            IServerStreamWriter<EventMessage> responses, ServerCallContext context)
        {
            state.Authenticate(context);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var message = requests.Current;
                if (message.SubscribeProjectEvent is not null)
                {
                    await responses.WriteAsync(new EventMessage
                    {
                        InvokeProjectHandler = new InvokeProjectHandler
                        {
                            EventName = message.SubscribeProjectEvent.EventNames.Single(),
                            Project = new ProjectConfig { Name = "test-project" },
                        },
                    });
                }
                else if (message.ProjectHandlerStatus is not null)
                {
                    state.HandlerStatus = message.ProjectHandlerStatus.Status;
                    return;
                }
            }
        }
    }
}
