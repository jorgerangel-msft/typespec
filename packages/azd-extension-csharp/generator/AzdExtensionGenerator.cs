// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Azd;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.TypeSpec.Generator;
using Microsoft.TypeSpec.Generator.Providers;
using TypeSpec.Azd.Generator.Input;

namespace TypeSpec.Azd.Generator;

[Export(typeof(CodeModelGenerator))]
[ExportMetadata(GeneratorMetadataName, nameof(AzdExtensionGenerator))]
public sealed class AzdExtensionGenerator : CodeModelGenerator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    [ImportingConstructor]
    public AzdExtensionGenerator(GeneratorContext context) : base(context)
    {
        InputLibrary = new AzdInputLibrary(Configuration.OutputDirectory);
        OutputLibrary = new ExtensionOutputLibrary(InputLibrary);
    }

    public override AzdInputLibrary InputLibrary { get; }
    public override OutputLibrary OutputLibrary { get; }

    protected override void Configure()
    {
        base.Configure();
        AddMetadataReference(MetadataReference.CreateFromFile(typeof(Command).Assembly.Location));
        AddMetadataReference(MetadataReference.CreateFromFile(typeof(ServiceCollection).Assembly.Location));
        AddMetadataReference(MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location));
        foreach (var type in new[] { typeof(Grpc.Net.Client.GrpcChannel), typeof(Grpc.Core.CallInvoker),
            typeof(Grpc.Core.Interceptors.Interceptor), typeof(ProjectConfig), typeof(Google.Protobuf.IMessage),
            typeof(System.Text.Json.JsonSerializer) })
        {
            AddMetadataReference(MetadataReference.CreateFromFile(type.Assembly.Location));
        }
    }

    public override async Task WriteAdditionalFiles(string outputPath)
    {
        var definition = InputLibrary.InputNamespace.Extension;
        var generated = Path.Combine(outputPath, "src", "Generated");
        var manifest = new
        {
            id = definition.Id,
            version = definition.Version,
            displayName = definition.DisplayName,
            description = definition.Description,
            @namespace = definition.Namespace,
            entryPoint = definition.EntryPoint,
            usage = $"azd {definition.Namespace} <command> [options]",
            requiredAzdVersion = ">=1.37.0",
            capabilities = new[] { "custom-commands", "metadata" },
        };
        // JSON is a YAML subset, so serialization also safely quotes author-provided strings.
        await File.WriteAllTextAsync(Path.Combine(generated, "extension.yaml"), JsonSerializer.Serialize(manifest, s_jsonOptions) + "\n");
        Directory.CreateDirectory(Path.Combine(generated, "proto"));
        const string protoPrefix = "TypeSpec.Azd.Generator.assets.upstream.proto.";
        foreach (var resource in Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(name => name.StartsWith(protoPrefix, StringComparison.Ordinal)))
        {
            var name = resource[protoPrefix.Length..];
            await File.WriteAllTextAsync(Path.Combine(generated, "proto", name), ReadAsset($"upstream.proto.{name}"));
        }
        await File.WriteAllTextAsync(Path.Combine(generated, "LICENSE.azd-template.txt"), ReadAsset("upstream.LICENSE.txt"));
        await File.WriteAllTextAsync(Path.Combine(generated, "azd-template.json"), ReadAsset("template-baseline.json"));
        var properties = new XElement("Project", new XElement("PropertyGroup",
            new XElement("AssemblyName", definition.EntryPoint),
            new XElement("Version", definition.Version),
            new XElement("RootNamespace", InputLibrary.InputNamespace.Name),
            new XElement("PublishSingleFile", new XAttribute("Condition", "'$(RuntimeIdentifier)' != ''"), "true"),
            new XElement("SelfContained", new XAttribute("Condition", "'$(RuntimeIdentifier)' != ''"), "true")),
            new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "Azure.Identity"), new XAttribute("Version", "1.13.2")),
                new XElement("PackageReference", new XAttribute("Include", "Azure.ResourceManager"), new XAttribute("Version", "1.13.0")),
                new XElement("PackageReference", new XAttribute("Include", "Google.Protobuf"), new XAttribute("Version", "3.30.2")),
                new XElement("PackageReference", new XAttribute("Include", "Grpc.Net.Client"), new XAttribute("Version", "2.70.0")),
                new XElement("PackageReference", new XAttribute("Include", "Grpc.Tools"), new XAttribute("Version", "2.71.0"),
                    new XElement("PrivateAssets", "all"),
                    new XElement("IncludeAssets", "runtime; build; native; contentfiles; analyzers; buildtransitive")),
                new XElement("PackageReference", new XAttribute("Include", "Microsoft.Extensions.DependencyInjection"), new XAttribute("Version", "9.0.4")),
                new XElement("PackageReference", new XAttribute("Include", "Spectre.Console"), new XAttribute("Version", "0.50.0")),
                new XElement("Protobuf", new XAttribute("Include", "proto\\*.proto"), new XAttribute("ProtoRoot", "proto"), new XAttribute("GrpcServices", "Client")),
                new XElement("EmbeddedResource", new XAttribute("Include", "LICENSE.azd-template.txt"),
                    new XAttribute("LogicalName", "TypeSpec.Azd.Template.License"))));
        await File.WriteAllTextAsync(Path.Combine(generated, "Extension.Generated.props"), properties + "\n");

        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"),
                new XElement("Nullable", "enable"), new XElement("ImplicitUsings", "enable"),
                new XElement("TreatWarningsAsErrors", "true")),
            new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "System.CommandLine"), new XAttribute("Version", "2.0.0")),
                new XElement("Compile", new XAttribute("Include", "..\\**\\*.cs"),
                    new XAttribute("Exclude", "..\\Generated\\**;..\\**\\obj\\**;..\\**\\bin\\**"))),
            new XElement("Import", new XAttribute("Project", "Extension.Generated.props")));
        await File.WriteAllTextAsync(Path.Combine(generated, "Extension.csproj"), project + "\n");
        foreach (var name in new[] { "build.ps1", "build.sh" })
        {
            var path = Path.Combine(generated, name);
            await File.WriteAllTextAsync(path, ReadAsset(name));
            if (name == "build.sh" && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(generated, ".gitignore"), ReadAsset("upstream.dotnet..gitignore"));
        await File.WriteAllTextAsync(Path.Combine(generated, "CHANGELOG.md"), $"# Release History\n\n## {definition.Version} - Initial Version\n");
    }

    internal static string CreateMetadata(InputExtension definition)
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            id = definition.Id,
            commands = new[]
            {
                new
                {
                    name = new[] { definition.Namespace },
                    @short = definition.Description,
                    subcommands = definition.Commands.Select(command => new
                    {
                        name = new[] { definition.Namespace, command.Name },
                        @short = command.Description,
                    }),
                },
            },
        }, s_jsonOptions);
    }

    private static string ReadAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TypeSpec.Azd.Generator.assets.{name}")
            ?? throw new InvalidOperationException($"Missing generator asset {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    private sealed class ExtensionOutputLibrary(AzdInputLibrary input) : OutputLibrary
    {
        protected override TypeProvider[] BuildTypeProviders()
        {
            var definition = input.InputNamespace.Extension;
            var options = new CommandOptionsProvider();
            var interceptor = new AuthHeaderInterceptorProvider();
            var client = new AzdClientProvider(interceptor);
            var projectArgs = new EventArgsProvider("ProjectEventArgs");
            var serviceArgs = new EventArgsProvider("ServiceEventArgs", service: true);
            var eventOptions = new EventArgsProvider("ServerEventOptions", options: true);
            TypeProvider[] commands = [.. definition.Commands.Select(command => new CommandProvider(command, options)), new MetadataCommandProvider(definition, options)];
            return [new ProgramProvider(definition, commands, client, options), .. commands, options, interceptor, client,
                projectArgs, serviceArgs, eventOptions, new EventManagerProvider(client, projectArgs, serviceArgs, eventOptions)];
        }
    }
}
