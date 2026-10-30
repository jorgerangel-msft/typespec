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

    [Test]
    public async Task ReportsHandlerFailuresInsteadOfSuccessfulCompletion()
    {
        using var client = new AzdClient(_address, AccessToken);
        await using var events = new EventManager(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await events.AddProjectEventHandlerAsync("preprovision",
            _ => Task.FromException(new InvalidOperationException("Failed domain rule.")), cancellation.Token);
        await events.ReceiveAsync(cancellation.Token);
        Assert.That(_state.HandlerStatus, Is.EqualTo("failed"));
        Assert.That(_state.HandlerMessage, Is.EqualTo("Failed domain rule."));
    }

    [Test]
    public async Task RegistersServiceFiltersAndDispatchesServiceHandlers()
    {
        using var client = new AzdClient(_address, AccessToken);
        await using var events = new EventManager(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? serviceName = null;
        await events.AddServiceEventHandlerAsync("prepackage", args =>
        {
            serviceName = args.Service.Name;
            return Task.CompletedTask;
        }, new ServerEventOptions { Host = "containerapp", Language = "csharp" }, cancellation.Token);
        await events.ReceiveAsync(cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceName, Is.EqualTo("test-service"));
            Assert.That(_state.ServiceHost, Is.EqualTo("containerapp"));
            Assert.That(_state.ServiceLanguage, Is.EqualTo("csharp"));
            Assert.That(_state.HandlerStatus, Is.EqualTo("completed"));
        }
    }

    [Test]
    public async Task PropagatesUnexpectedStreamFailures()
    {
        _state.FailStream = true;
        using var client = new AzdClient(_address, AccessToken);
        await using var events = new EventManager(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exception = Assert.ThrowsAsync<RpcException>(async () => await events.ReceiveAsync(cancellation.Token));
        Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Unavailable));
    }

    [Test]
    public void HonorsCancellationBeforeSubscribing()
    {
        using var client = new AzdClient(_address, AccessToken);
        var events = new EventManager(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            events.AddProjectEventHandlerAsync("preprovision", _ => Task.CompletedTask, cancellation.Token).GetAwaiter().GetResult());
        events.DisposeAsync().GetAwaiter().GetResult();
    }

    [TestCase("unary")]
    [TestCase("server-stream")]
    [TestCase("client-stream")]
    [TestCase("duplex")]
    public void AuthenticatesEverySupportedCallShape(string shape)
    {
        var marshaller = Marshallers.Create<EmptyRequest>(_ => [], _ => new EmptyRequest());
        var method = new Method<EmptyRequest, EmptyRequest>(MethodType.DuplexStreaming, "Test", "Call", marshaller, marshaller);
        var context = new Grpc.Core.Interceptors.ClientInterceptorContext<EmptyRequest, EmptyRequest>(
            method, "localhost", new CallOptions());
        var interceptor = new AuthHeaderInterceptor(AccessToken);
        string? authorization = null;
        void Capture(Grpc.Core.Interceptors.ClientInterceptorContext<EmptyRequest, EmptyRequest> call)
        {
            authorization = call.Options.Headers?.GetValue("authorization");
        }
        switch (shape)
        {
            case "unary":
                interceptor.AsyncUnaryCall(new EmptyRequest(), context, (_, call) => { Capture(call); return null!; });
                break;
            case "server-stream":
                interceptor.AsyncServerStreamingCall(new EmptyRequest(), context, (_, call) => { Capture(call); return null!; });
                break;
            case "client-stream":
                interceptor.AsyncClientStreamingCall(context, call => { Capture(call); return null!; });
                break;
            case "duplex":
                interceptor.AsyncDuplexStreamingCall(context, call => { Capture(call); return null!; });
                break;
        }
        Assert.That(authorization, Is.EqualTo(AccessToken));
    }

    [TestCase("", AccessToken)]
    [TestCase("http://localhost:12345", "")]
    public void RejectsMissingHostConnectionDetails(string address, string token)
    {
        Assert.Throws<ArgumentException>(() => _ = new AzdClient(address, token));
    }

    public sealed class RpcState
    {
        public string? Authorization { get; set; }
        public string? Method { get; set; }
        public string? HandlerStatus { get; set; }
        public string? HandlerMessage { get; set; }
        public string? ServiceHost { get; set; }
        public string? ServiceLanguage { get; set; }
        public bool FailStream { get; set; }

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
            if (state.FailStream)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Test host unavailable."));
            }
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
                    state.HandlerMessage = message.ProjectHandlerStatus.Message;
                    return;
                }
                else if (message.SubscribeServiceEvent is not null)
                {
                    state.ServiceHost = message.SubscribeServiceEvent.Host;
                    state.ServiceLanguage = message.SubscribeServiceEvent.Language;
                    await responses.WriteAsync(new EventMessage
                    {
                        InvokeServiceHandler = new InvokeServiceHandler
                        {
                            EventName = message.SubscribeServiceEvent.EventNames.Single(),
                            Project = new ProjectConfig { Name = "test-project" },
                            Service = new ServiceConfig { Name = "test-service" },
                        },
                    });
                }
                else if (message.ServiceHandlerStatus is not null)
                {
                    state.HandlerStatus = message.ServiceHandlerStatus.Status;
                    return;
                }
            }
        }
    }
}
