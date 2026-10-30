// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Azd;
using Microsoft.TypeSpec.Generator.Expressions;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;
using static Microsoft.TypeSpec.Generator.Snippets.Snippet;

namespace TypeSpec.Azd.Generator;

internal sealed class AzdClientProvider(AuthHeaderInterceptorProvider interceptor) : SupportTypeProvider("AzdClient")
{
    private FieldProvider? _channel;
    private FieldProvider Channel => _channel ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly,
        typeof(GrpcChannel), "_channel", this);
    private static readonly (string Name, Type Type)[] s_services =
    [
        ("Deployment", typeof(DeploymentService.DeploymentServiceClient)),
        ("Environment", typeof(EnvironmentService.EnvironmentServiceClient)),
        ("Events", typeof(EventService.EventServiceClient)),
        ("Project", typeof(ProjectService.ProjectServiceClient)),
        ("Prompt", typeof(PromptService.PromptServiceClient)),
        ("UserConfig", typeof(UserConfigService.UserConfigServiceClient)),
        ("Workflow", typeof(WorkflowService.WorkflowServiceClient)),
    ];

    protected override FieldProvider[] BuildFields() => [Channel];
    protected override CSharpType[] BuildImplements() => [typeof(IDisposable)];
    protected override PropertyProvider[] BuildProperties() => s_services.Select(service =>
        new PropertyProvider(null, MethodSignatureModifiers.Public, new ServiceClientReference(service.Type).Type,
            service.Name, new AutoPropertyBody(false), this)).ToArray();

    protected override ConstructorProvider[] BuildConstructors()
    {
        var address = new ParameterProvider("serverAddress", $"The azd server address.", typeof(string));
        var token = new ParameterProvider("accessToken", $"The azd invocation token.", typeof(string));
        ValueExpression EnvironmentValue(string name) =>
            Static(typeof(System.Environment)).Invoke("GetEnvironmentVariable", Literal(name))
                .NullCoalesce(ThrowExpression(New.InvalidOperationException(Literal($"{name} is required to call azd services."))));
        var body = new List<MethodBodyStatement>
        {
            Static(typeof(ArgumentException)).Invoke("ThrowIfNullOrEmpty", address).Terminate(),
            Static(typeof(ArgumentException)).Invoke("ThrowIfNullOrEmpty", token).Terminate(),
            new IfStatement(Not(address.Invoke("Contains", Literal("://"))))
            {
                address.Assign(new BinaryOperatorExpression("+", Literal("http://"), address)).Terminate(),
            },
            Channel.Assign(Static(typeof(GrpcChannel)).Invoke("ForAddress", address)).Terminate(),
            Declare("callInvoker", typeof(CallInvoker),
                Static(typeof(CallInvokerExtensions)).Invoke("Intercept",
                    [Channel.AsValueExpression.Invoke("CreateCallInvoker"), New.Instance(interceptor.Type, token)]), out var invoker),
        };
        foreach (var service in s_services)
        {
            body.Add(This.Property(service.Name).Assign(New.Instance(new ServiceClientReference(service.Type).Type, invoker)).Terminate());
        }
        return
        [
            new ConstructorProvider(new ConstructorSignature(Type, null, MethodSignatureModifiers.Public, [],
                initializer: new ConstructorInitializer(false, [EnvironmentValue("AZD_SERVER"), EnvironmentValue("AZD_ACCESS_TOKEN")])),
                Array.Empty<MethodBodyStatement>(), this),
            new ConstructorProvider(new ConstructorSignature(Type, null, MethodSignatureModifiers.Public, [address, token]), body, this),
        ];
    }

    protected override MethodProvider[] BuildMethods() =>
        [Method("Dispose", null, [], Channel.AsValueExpression.Invoke("Dispose").Terminate())];

    // Grpc's ClientBase<TSelf> has a self-referencing base; reference the nested type without reflecting that base graph.
    private sealed class ServiceClientReference(Type type) : TypeProvider
    {
        protected override string BuildName() => type.Name;
        protected override string BuildNamespace() => type.Namespace!;
        protected override string BuildRelativeFilePath() => throw new InvalidOperationException("External client references are not emitted.");
        protected override TypeProvider? BuildDeclaringTypeProvider() =>
            type.DeclaringType is { } declaring ? new ServiceClientReference(declaring) : null;
        protected override TypeSignatureModifiers BuildDeclarationModifiers() => TypeSignatureModifiers.Public;
    }
}

internal sealed class AuthHeaderInterceptorProvider : SupportTypeProvider
{
    public AuthHeaderInterceptorProvider() : base("AuthHeaderInterceptor") { }
    private FieldProvider? _token;
    private FieldProvider Token => _token ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly, typeof(string), "_accessToken", this);
    protected override CSharpType? BuildBaseType() => typeof(Interceptor);
    protected override FieldProvider[] BuildFields() => [Token];
    protected override ConstructorProvider[] BuildConstructors()
    {
        var token = new ParameterProvider("accessToken", $"The azd invocation token.", typeof(string));
        return
        [
            new ConstructorProvider(new ConstructorSignature(Type, null, MethodSignatureModifiers.Public, [token]),
                new MethodBodyStatement[]
                {
                    Static(typeof(ArgumentException)).Invoke("ThrowIfNullOrEmpty", token).Terminate(),
                    Token.Assign(token).Terminate(),
                }, this),
        ];
    }

    private sealed class Parameters<TRequest, TResponse> { }

    protected override MethodProvider[] BuildMethods()
    {
        CSharpType[] arguments = typeof(Parameters<,>).GetGenericArguments().Select(type => new CSharpType(type)).ToArray();
        var contextType = new CSharpType(typeof(ClientInterceptorContext<,>), arguments);
        var methods = new List<MethodProvider>();
        foreach (var (name, resultType, continuationType, hasRequest) in new[]
        {
            ("AsyncUnaryCall", typeof(AsyncUnaryCall<>), typeof(Interceptor.AsyncUnaryCallContinuation<,>), true),
            ("AsyncServerStreamingCall", typeof(AsyncServerStreamingCall<>), typeof(Interceptor.AsyncServerStreamingCallContinuation<,>), true),
            ("AsyncClientStreamingCall", typeof(AsyncClientStreamingCall<,>), typeof(Interceptor.AsyncClientStreamingCallContinuation<,>), false),
            ("AsyncDuplexStreamingCall", typeof(AsyncDuplexStreamingCall<,>), typeof(Interceptor.AsyncDuplexStreamingCallContinuation<,>), false),
        })
        {
            var request = new ParameterProvider("request", $"The request.", arguments[0]);
            var context = new ParameterProvider("context", $"The call context.", contextType);
            var continuation = new ParameterProvider("continuation", $"The continuation.", new CSharpType(continuationType, arguments));
            ParameterProvider[] parameters = hasRequest ? [request, context, continuation] : [context, continuation];
            var body = new List<MethodBodyStatement>
            {
                Declare("headers", typeof(Metadata),
                    context.Property("Options").Property("Headers").NullCoalesce(New.Instance(typeof(Metadata))), out var headers),
                headers.Invoke("Add", [Literal("authorization"), Token]).Terminate(),
                Declare("authenticated", contextType, New.Instance(contextType,
                    context.Property("Method"), context.Property("Host"),
                    context.Property("Options").Invoke("WithHeaders", headers)), out var authenticated),
                Return(new InvokeMethodExpression(null, "continuation",
                    hasRequest ? [request, authenticated] : [authenticated])),
            };
            methods.Add(new MethodProvider(new MethodSignature(name, null,
                MethodSignatureModifiers.Public | MethodSignatureModifiers.Override,
                new CSharpType(resultType, hasRequest ? [arguments[1]] : arguments), null, parameters, GenericArguments: arguments),
                body, this));
        }
        return [.. methods];
    }
}
