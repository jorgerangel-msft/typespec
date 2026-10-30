// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.TypeSpec.Generator.Expressions;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;
using static Microsoft.TypeSpec.Generator.Snippets.Snippet;

namespace TypeSpec.Azd.Generator;

internal sealed class CommandOptionsProvider() : SupportTypeProvider("CommandOptions", "TypeSpec.Azd.Generated")
{
    private FieldProvider? _output;
    private FieldProvider? _cwd;
    private FieldProvider Output => _output ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly,
        typeof(Option<string>), "_output", this, initializationValue: New.Instance(typeof(Option<string>), Literal("--output"),
            new NewArrayExpression(typeof(string), new ArrayInitializerExpression([Literal("-o")]))));
    private FieldProvider Cwd => _cwd ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly,
        typeof(Option<string>), "_cwd", this, initializationValue: New.Instance(typeof(Option<string>), Literal("--cwd"),
            new NewArrayExpression(typeof(string), new ArrayInitializerExpression([Literal("-C")]))));
    protected override FieldProvider[] BuildFields() => [Output, Cwd];

    protected override MethodProvider[] BuildMethods()
    {
        var root = new ParameterProvider("root", $"The root command.", typeof(RootCommand));
        var configure = new List<MethodBodyStatement>
        {
            Output.Property("Recursive").Assign(True).Terminate(),
            Output.Property("Description").Assign(Literal("Output format: default, json, or none.")).Terminate(),
            Output.Property("DefaultValueFactory").Assign(new FuncExpression([null], Literal("default"))).Terminate(),
            Output.AsValueExpression.Invoke("AcceptOnlyFromAmong", New.Array(typeof(string), Literal("default"), Literal("json"), Literal("none"))).Terminate(),
            Cwd.Property("Recursive").Assign(True).Terminate(),
            Cwd.Property("Description").Assign(Literal("Sets the current working directory.")).Terminate(),
            root.Property("Options").Invoke("Add", Output).Terminate(),
            root.Property("Options").Invoke("Add", Cwd).Terminate(),
        };
        foreach (var (name, alias, type, hidden) in new[]
        {
            ("environment", "e", typeof(string), false), ("debug", "", typeof(bool), false),
            ("no-prompt", "", typeof(bool), false), ("docs", "", typeof(bool), false),
            ("trace-log-file", "", typeof(string), true), ("trace-log-url", "", typeof(string), true),
        })
        {
            var properties = new Dictionary<ValueExpression, ValueExpression> { [Identifier("Recursive")] = True };
            if (hidden) properties[Identifier("Hidden")] = True;
            configure.Add(root.Property("Options").Invoke("Add",
                new NewInstanceExpression(new CSharpType(typeof(Option<>), type),
                    [Literal($"--{name}"), New.Array(typeof(string), alias.Length == 0 ? [] : [Literal($"-{alias}")])],
                    new ObjectInitializerExpression(properties))).Terminate());
        }
        var args = new ParameterProvider("args", $"Command-line arguments.", typeof(string[]));
        var parse = Declare("result", typeof(ParseResult), root.Invoke("Parse", [args, Null]), out var result);
        var directory = Declare("cwd", typeof(string),
            result.Invoke("GetValue", [Cwd], [typeof(string)])
                .NullCoalesce(Static(typeof(Environment)).Invoke("GetEnvironmentVariable", Literal("AZD_CWD"))), out var cwd);
        var errorDeclaration = Declare("error", typeof(Exception), out var error);
        var run = new MethodBodyStatement[]
        {
            parse,
            new IfStatement(new BinaryOperatorExpression("!=", result.Property("Errors").Property("Count"), Literal(0)))
            {
                Return(result.Invoke("InvokeAsync", [Null], async: true)),
            },
            directory,
            new IfStatement(Not(Static(typeof(string)).Invoke("IsNullOrEmpty", cwd)))
            {
                new TryCatchFinallyStatement(new TryExpression(Static(typeof(Directory)).Invoke("SetCurrentDirectory", cwd).Terminate()),
                    new CatchExpression(errorDeclaration,
                        new IfStatement(Not(new BinaryOperatorExpression("||",
                            new BinaryOperatorExpression("||", new BinaryOperatorExpression("is", error, (ValueExpression)typeof(IOException)),
                                new BinaryOperatorExpression("is", error, (ValueExpression)typeof(UnauthorizedAccessException))),
                            new BinaryOperatorExpression("is", error, (ValueExpression)typeof(ArgumentException))))) { Throw() },
                        Static(typeof(Console)).Property("Error").Invoke("WriteLine", error.Property("Message")).Terminate(),
                        Return(Literal(1)))),
            },
            Return(result.Invoke("InvokeAsync", [Null], async: true)),
        };
        var parsed = new ParameterProvider("result", $"The parsed command.", typeof(ParseResult));
        var value = new ParameterProvider("value", $"The command result.", typeof(string));
        var format = Declare("format", typeof(string), parsed.Invoke("GetValue", [Output], [typeof(string)]), out var output);
        return
        [
            Method("ConfigureRoot", null, [root], configure),
            Method("RunAsync", typeof(Task<int>), [args, root], run, MethodSignatureModifiers.Public | MethodSignatureModifiers.Async),
            Method("WriteResult", typeof(int), [parsed, value], new MethodBodyStatement[]
            {
                format,
                new IfStatement(new BinaryOperatorExpression("==", output, Literal("none"))) { Return(Literal(0)) },
                new IfStatement(new BinaryOperatorExpression("==", output, Literal("json")))
                {
                    InvokeConsoleWriteLine(Static(typeof(JsonSerializer)).Invoke("Serialize", [value, Null.CastTo(typeof(JsonSerializerOptions))], [typeof(string)])),
                    Return(Literal(0)),
                },
                InvokeConsoleWriteLine(value),
                Return(Literal(0)),
            }),
            Method("WriteMetadata", typeof(int), [value], new MethodBodyStatement[]
            {
                InvokeConsoleWriteLine(value), Return(Literal(0)),
            }, MethodSignatureModifiers.Public | MethodSignatureModifiers.Static),
        ];
    }
}
