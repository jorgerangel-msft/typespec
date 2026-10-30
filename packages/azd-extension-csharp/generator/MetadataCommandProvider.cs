// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.TypeSpec.Generator.Expressions;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using TypeSpec.Azd.Generator.Input;
using static Microsoft.TypeSpec.Generator.Snippets.Snippet;

namespace TypeSpec.Azd.Generator;

internal sealed class MetadataCommandProvider(InputExtension definition, CommandOptionsProvider commandOptions) : TypeProvider
{
    protected override string BuildName() => "MetadataCommand";
    protected override TypeSignatureModifiers BuildDeclarationModifiers() => TypeSignatureModifiers.Public | TypeSignatureModifiers.Sealed;
    protected override string BuildRelativeFilePath() => Path.Combine("src", "Generated", "commands", $"{Name}.cs");

    protected override MethodProvider[] BuildMethods()
    {
        var create = Declare("command", typeof(Command),
            New.Instance(typeof(Command), Literal("metadata"), Literal("Describes this extension to azd.")), out var command);
        return
        [
            new MethodProvider(new MethodSignature("Build", null, MethodSignatureModifiers.Public, typeof(Command), null, []),
                new[]
                {
                    create,
                    command.Property("Hidden").Assign(True).Terminate(),
                    command.Invoke("SetAction", new FuncExpression([null],
                        Static(commandOptions.Type).Invoke("WriteMetadata",
                            Literal(AzdExtensionGenerator.CreateMetadata(definition))))).Terminate(),
                    Return(command),
                }, this),
        ];
    }
}
