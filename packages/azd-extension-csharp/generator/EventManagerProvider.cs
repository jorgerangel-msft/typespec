// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Grpc.Core;
using Microsoft.Azd;
using Microsoft.TypeSpec.Generator.Expressions;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;
using static Microsoft.TypeSpec.Generator.Snippets.Snippet;

namespace TypeSpec.Azd.Generator;

internal sealed class EventArgsProvider(string name, bool service = false, bool options = false) : SupportTypeProvider(name)
{
    protected override PropertyProvider[] BuildProperties() => options
        ? [Property("Host", new CSharpType(typeof(string), true)), Property("Language", new CSharpType(typeof(string), true))]
        : service ? [Property("Project", typeof(ProjectConfig)), Property("Service", typeof(ServiceConfig))]
        : [Property("Project", typeof(ProjectConfig))];

    private PropertyProvider Property(string name, CSharpType type) =>
        new(null, MethodSignatureModifiers.Public, type, name, new AutoPropertyBody(true), this);
}

internal sealed class EventManagerProvider(AzdClientProvider client, EventArgsProvider projectArgs,
    EventArgsProvider serviceArgs, EventArgsProvider options) : SupportTypeProvider("EventManager")
{
    private FieldProvider? _client;
    private FieldProvider? _stream;
    private FieldProvider? _projects;
    private FieldProvider? _services;
    private FieldProvider? _writeLock;
    private FieldProvider Client => _client ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly, client.Type, "_azdClient", this);
    private FieldProvider Stream => _stream ??= new(FieldModifiers.Private, typeof(AsyncDuplexStreamingCall<EventMessage, EventMessage>), "_stream", this);
    private CSharpType HandlerType(bool service) => new(typeof(Func<,>), service ? serviceArgs.Type : projectArgs.Type, typeof(Task));
    private FieldProvider Handlers(bool service)
    {
        ref var field = ref (service ? ref _services : ref _projects);
        var type = new CSharpType(typeof(Dictionary<,>), typeof(string), HandlerType(service));
        return field ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly, type, service ? "_serviceHandlers" : "_projectHandlers",
            this, initializationValue: New.Instance(type));
    }
    private FieldProvider WriteLock => _writeLock ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly, typeof(SemaphoreSlim), "_writeLock",
        this, initializationValue: New.Instance(typeof(SemaphoreSlim), Literal(1), Literal(1)));
    protected override FieldProvider[] BuildFields() => [Client, Stream, Handlers(false), Handlers(true), WriteLock];
    protected override CSharpType[] BuildImplements() => [typeof(IAsyncDisposable)];
    protected override ConstructorProvider[] BuildConstructors()
    {
        var value = new ParameterProvider("azdClient", $"The azd client.", client.Type);
        return
        [
            new ConstructorProvider(new ConstructorSignature(Type, null, MethodSignatureModifiers.Public, [value]),
                Client.Assign(value).Terminate(), this),
        ];
    }

    protected override MethodProvider[] BuildMethods()
    {
        var cancellation = new ParameterProvider("cancellationToken", $"The invocation cancellation token.", typeof(CancellationToken));
        var message = new ParameterProvider("message", $"The event message.", typeof(EventMessage));
        var methods = new List<MethodProvider>
        {
            Method("EnsureInitialized", null, [cancellation], new MethodBodyStatement[]
            {
                cancellation.Invoke("ThrowIfCancellationRequested").Terminate(),
                new IfStatement(new BinaryOperatorExpression("==", Stream, Null))
                {
                    Stream.Assign(Client.Property("Events").Invoke("EventStream", [Null, Null, cancellation])).Terminate(),
                },
            }, MethodSignatureModifiers.Private),
            Method("SendAsync", typeof(Task), [message, cancellation], new MethodBodyStatement[]
            {
                This.Invoke("EnsureInitialized", cancellation).Terminate(),
                WriteLock.AsValueExpression.Invoke("WaitAsync", [cancellation], true).Terminate(),
                new TryCatchFinallyStatement(new TryExpression(Stream.Property("RequestStream").Invoke("WriteAsync", [message, cancellation], true).Terminate()), [],
                    new FinallyExpression(WriteLock.AsValueExpression.Invoke("Release").Terminate())),
            }, MethodSignatureModifiers.Private | MethodSignatureModifiers.Async),
            Method("DisposeAsync", typeof(ValueTask), [], new MethodBodyStatement[]
            {
                new TryCatchFinallyStatement(new TryExpression(
                    new IfStatement(new BinaryOperatorExpression("!=", Stream, Null))
                    {
                        Stream.Property("RequestStream").Invoke("CompleteAsync", [], true).Terminate(),
                    }), [], new FinallyExpression(
                        Stream.NullConditional().Invoke("Dispose").Terminate(),
                        WriteLock.AsValueExpression.Invoke("Dispose").Terminate())),
            }, MethodSignatureModifiers.Public | MethodSignatureModifiers.Async),
        };
        var receive = new List<MethodBodyStatement> { This.Invoke("EnsureInitialized", cancellation).Terminate() };
        var loop = new WhileStatement(Stream.Property("ResponseStream").Invoke("MoveNext", [cancellation], true));
        loop.Add(Declare("message", typeof(EventMessage), Stream.Property("ResponseStream").Property("Current"), out var current));
        loop.Add(new IfStatement(new BinaryOperatorExpression("!=", current.Property("InvokeProjectHandler"), Null))
        {
            This.Invoke("HandleProjectEventAsync", [current.Property("InvokeProjectHandler"), cancellation], true).Terminate(),
        });
        loop.Add(new IfStatement(new BinaryOperatorExpression("!=", current.Property("InvokeServiceHandler"), Null))
        {
            This.Invoke("HandleServiceEventAsync", [current.Property("InvokeServiceHandler"), cancellation], true).Terminate(),
        });
        receive.Add(loop);
        methods.Add(Method("ReceiveAsync", typeof(Task), [cancellation], receive,
            MethodSignatureModifiers.Public | MethodSignatureModifiers.Async));
        foreach (var service in new[] { false, true })
        {
            methods.Add(BuildSubscribe(service, cancellation));
            methods.Add(BuildDispatch(service, cancellation));
            var name = new ParameterProvider("eventName", $"The lifecycle event.", typeof(string));
            methods.Add(Method(service ? "RemoveServiceEventHandler" : "RemoveProjectEventHandler", null, [name],
                Handlers(service).AsValueExpression.Invoke("Remove", name).Terminate()));
        }
        return [.. methods];
    }

    private MethodProvider BuildSubscribe(bool service, ParameterProvider cancellation)
    {
        var name = new ParameterProvider("eventName", $"The lifecycle event.", typeof(string));
        var handler = new ParameterProvider("handler", $"The lifecycle handler.", HandlerType(service));
        var filter = new ParameterProvider("options", $"Service event filters.", options.Type.WithNullable(true));
        var subscriptionType = service ? typeof(SubscribeServiceEvent) : typeof(SubscribeProjectEvent);
        var body = new List<MethodBodyStatement>
        {
            new AssignmentExpression(new IndexerExpression(Handlers(service), name), handler).Terminate(),
            Declare("subscription", subscriptionType, New.Instance(subscriptionType), out var subscription),
            subscription.Property("EventNames").Invoke("Add", name).Terminate(),
        };
        if (service)
        {
            body.Add(new IfStatement(new BinaryOperatorExpression("!=", filter, Null))
            {
                subscription.Property("Host").Assign(filter.Property("Host").NullCoalesce(Literal(""))).Terminate(),
                subscription.Property("Language").Assign(filter.Property("Language").NullCoalesce(Literal(""))).Terminate(),
            });
        }
        body.Add(Return(This.Invoke("SendAsync", [New.Instance(typeof(EventMessage),
            new Dictionary<ValueExpression, ValueExpression> { [Identifier(service ? "SubscribeServiceEvent" : "SubscribeProjectEvent")] = subscription }), cancellation])));
        return Method(service ? "AddServiceEventHandlerAsync" : "AddProjectEventHandlerAsync", typeof(Task),
            service ? [name, handler, filter, cancellation] : [name, handler, cancellation], body);
    }

    private MethodProvider BuildDispatch(bool service, ParameterProvider cancellation)
    {
        var invocation = new ParameterProvider("invocation", $"The lifecycle invocation.",
            service ? typeof(InvokeServiceHandler) : typeof(InvokeProjectHandler));
        var name = invocation.Property("EventName");
        var handlers = Handlers(service);
        var body = new List<MethodBodyStatement>
        {
            new IfStatement(Not(handlers.AsValueExpression.Invoke("ContainsKey", name)))
            {
                Throw(New.InvalidOperationException(Literal("No handler is registered for the received lifecycle event."))),
            },
            Declare("status", typeof(string), Literal("completed"), out var status),
            Declare("message", typeof(string), Literal(""), out var message),
        };
        var properties = new Dictionary<ValueExpression, ValueExpression> { [Identifier("Project")] = invocation.Property("Project") };
        if (service) properties[Identifier("Service")] = invocation.Property("Service");
        var errorDeclaration = Declare("error", typeof(Exception), out var error);
        body.Add(new TryCatchFinallyStatement(
            new TryExpression(new IndexerExpression(handlers, name).Invoke("Invoke",
                [New.Instance(service ? serviceArgs.Type : projectArgs.Type, properties)], true).Terminate()),
            new CatchExpression(errorDeclaration,
                status.Assign(Literal("failed")).Terminate(),
                message.Assign(error.Property("Message")).Terminate(),
                Static(typeof(Console)).Property("Error").Invoke("WriteLine", error.Property("Message")).Terminate())));
        var statusProperties = new Dictionary<ValueExpression, ValueExpression>
        {
            [Identifier("EventName")] = name,
            [Identifier("Status")] = status,
            [Identifier("Message")] = message,
        };
        if (service) statusProperties[Identifier("ServiceName")] = invocation.Property("Service").Property("Name");
        var response = New.Instance(service ? typeof(ServiceHandlerStatus) : typeof(ProjectHandlerStatus), statusProperties);
        body.Add(This.Invoke("SendAsync", [New.Instance(typeof(EventMessage), new Dictionary<ValueExpression, ValueExpression>
        {
            [Identifier(service ? "ServiceHandlerStatus" : "ProjectHandlerStatus")] = response,
        }), cancellation], true).Terminate());
        return Method(service ? "HandleServiceEventAsync" : "HandleProjectEventAsync", typeof(Task), [invocation, cancellation], body,
            MethodSignatureModifiers.Private | MethodSignatureModifiers.Async);
    }
}
