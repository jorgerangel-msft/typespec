// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.TypeSpec.Generator;
using Microsoft.TypeSpec.Generator.Expressions;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using TypeSpec.Azd.Generator.Input;
using static Microsoft.TypeSpec.Generator.Snippets.Snippet;

namespace TypeSpec.Azd.Generator;

internal sealed class CommandProvider(InputCommand command, CommandOptionsProvider commandOptions) : TypeProvider
{
    private FieldProvider? _options;
    private FieldProvider Options => _options ??= new(FieldModifiers.Private | FieldModifiers.ReadOnly,
        commandOptions.Type, "_options", this);

    protected override string BuildName() => char.ToUpperInvariant(command.Name[0]) + command.Name[1..].Replace('-', '_') + "Command";
    protected override TypeSignatureModifiers BuildDeclarationModifiers() => TypeSignatureModifiers.Public | TypeSignatureModifiers.Sealed;
    protected override string BuildRelativeFilePath() => Path.Combine("src", "Generated", "commands", $"{Name}.cs");
    protected override FieldProvider[] BuildFields() => [Options];

    protected override ConstructorProvider[] BuildConstructors()
    {
        var options = new ParameterProvider("options", $"Shared command options.", Options.Type);
        return
        [
            new ConstructorProvider(new ConstructorSignature(Type, null, MethodSignatureModifiers.Public, [options]),
                Options.Assign(options).Terminate(), this),
        ];
    }

    protected override MethodProvider[] BuildMethods()
    {
        var result = new VariableExpression(typeof(ParseResult), "result");
        var create = Declare("command", typeof(Command),
            New.Instance(typeof(Command), Literal(command.Name), Literal(command.Description)), out var cliCommand);
        var returnType = CodeModelGenerator.Instance.TypeFactory.CreateCSharpType(command.Result.ValueType)
            ?? throw new InvalidOperationException($"Cannot generate result type for command '{command.Name}'.");
        return
        [
            new MethodProvider(new MethodSignature("Build", null, MethodSignatureModifiers.Public, typeof(Command), null, []),
                new[]
                {
                    create,
                    cliCommand.Invoke("SetAction", new FuncExpression([result.Declaration],
                        Options.Invoke("WriteResult", [result, This.Invoke("Execute")]))).Terminate(),
                    Return(cliCommand),
                }, this),
            new MethodProvider(new MethodSignature("Execute", null, MethodSignatureModifiers.Private, returnType, null, []),
                Literal(command.Result.Value), this),
        ];
    }
}
