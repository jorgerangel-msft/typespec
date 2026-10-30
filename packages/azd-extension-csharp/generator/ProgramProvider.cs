// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Azd;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;
using TypeSpec.Azd.Generator.Input;
using static Microsoft.TypeSpec.Generator.Snippets.Snippet;

namespace TypeSpec.Azd.Generator;

internal sealed class ProgramProvider(InputExtension definition, IReadOnlyList<TypeProvider> commands) : TypeProvider
{
    protected override string BuildName() => "Program";
    protected override TypeSignatureModifiers BuildDeclarationModifiers() => TypeSignatureModifiers.Internal | TypeSignatureModifiers.Static;
    protected override string BuildRelativeFilePath() => Path.Combine("src", "Generated", "Program.cs");

    protected override MethodProvider[] BuildMethods()
    {
        var args = new ParameterProvider("args", $"Command-line arguments.", typeof(string[]));
        var statements = new List<MethodBodyStatement>
        {
            Declare("services", typeof(ServiceCollection), New.Instance(typeof(ServiceCollection)), out var services),
            services.Invoke("AddSingleton", [], [typeof(AzdClient)]).Terminate(),
            services.Invoke("AddSingleton", [], [typeof(global::TypeSpec.Azd.Generated.CommandOptions)]).Terminate(),
        };
        foreach (var command in commands)
        {
            statements.Add(services.Invoke("AddTransient", [], [command.Type]).Terminate());
        }
        statements.Add(UsingDeclare("serviceProvider", typeof(ServiceProvider),
            services.Invoke("BuildServiceProvider"), out var serviceProvider));
        statements.Add(Declare("rootCommand", typeof(RootCommand),
            New.Instance(typeof(RootCommand), Literal(definition.Description)), out var root));
        statements.Add(Declare("options", typeof(global::TypeSpec.Azd.Generated.CommandOptions),
            serviceProvider.Invoke("GetRequiredService", [], [typeof(global::TypeSpec.Azd.Generated.CommandOptions)]), out var options));
        statements.Add(options.Invoke("ConfigureRoot", root).Terminate());
        foreach (var command in commands)
        {
            statements.Add(root.Property("Subcommands").Invoke("Add",
                serviceProvider.Invoke("GetRequiredService", [], [command.Type]).Invoke("Build")).Terminate());
        }
        statements.Add(Return(options.Invoke("RunAsync", [args, root], async: true)));
        return
        [
            new MethodProvider(
                new MethodSignature("Main", null, MethodSignatureModifiers.Public | MethodSignatureModifiers.Static | MethodSignatureModifiers.Async,
                    typeof(Task<int>), null, [args]),
                statements, this),
        ];
    }
}
