# TypeSpec azd extension prototype

An experimental, **zero-custom-code** reproduction of the [Contoso Resource Tagger quickstart](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/extensions/develop/quickstart-build-extension).

The quickstart's first command prints a greeting; it does not yet read or modify resource tags. This prototype generates that complete extension from TypeSpec, not an interface that the extension author must implement.

## Authoring

The entire sample implementation is [`samples/tagger/main.tsp`](samples/tagger/main.tsp):

```typespec
import "@typespec/azd-extension-csharp";

using TypeSpec.Azd;

@extension(#{
  id: "contoso.azd.tagger",
  commandNamespace: "tagger",
  version: "0.1.0",
  displayName: "Contoso Resource Tagger",
})
@doc("Standardize and report Azure resource tags for an azd project.")
namespace Contoso.ResourceTagger;

@doc("Displays a greeting from the Contoso Resource Tagger extension.")
@command
op show(): "Hello from the Contoso Resource Tagger extension!";
```

`@extension` supplies the manifest identity and CLI namespace. `@command` exposes an operation; an optional string argument overrides its CLI name. `@doc` supplies help and metadata. A string-literal return type explicitly specifies the command's behavior: return that string and render it to the selected output. There is no authored C#, command registration, or manifest duplication.

The initial supported subset is deliberately small: one extension, flat parameterless commands, and string-literal results. Unsupported parameters/result types, duplicate or reserved command names, missing implementations, and invalid identities produce compiler errors rather than incomplete extensions.

## Build and generate

Prerequisites: Node.js compatible with TypeSpec 1.17, npm, .NET 10 SDK, and this repository checkout. For azd installation, use azd 1.37 or later; the developer extension supplies `azd x pack`.

This private prototype uses **npm independently of the pnpm workspace**, like the language emitter packages. It references MTG's source project in the adjacent `http-client-csharp` package. It is not a published npm/NuGet product and does not change MTG.

From `packages/azd-extension-csharp`:

```powershell
npm ci
npm run build
npm test
npm run generate:sample
```

Output is under `samples/tagger/tsp-output/@typespec/azd-extension-csharp`:

`generate:sample` explicitly sets `save-inputs=true`, retaining `tspCodeModel.json` and `Configuration.json` for inspection. Retention defaults to `true`; set the emitter's `save-inputs` option to `false` to remove those files after successful generation. Inputs are always retained when generation fails.

| Output                            | Purpose                                                                    |
| --------------------------------- | -------------------------------------------------------------------------- |
| `tspCodeModel.json`               | MTG code-model root plus a versioned extension section                     |
| `Configuration.json`              | MTG pipeline configuration                                                 |
| `src/Generated/Program.cs`        | Template-style entry point with DI registrations and a root command        |
| `src/Generated/commands/*.cs`     | One DI-registered class with `Build()` per authored command, plus metadata |
| `src/Generated/AzdClient.cs`      | Upstream-style authenticated client for azd's gRPC services                |
| `src/Generated/EventManager.cs`   | Upstream lifecycle event subscription and dispatch helper                  |
| `src/Generated/CommandOptions.cs` | Shared CLI options, working-directory handling, and output formatting      |
| `proto/*.proto`                   | Pinned upstream scaffold contracts in the `Microsoft.Azd` C# namespace     |
| `extension.yaml`                  | Manifest with `custom-commands` and `metadata` capabilities                |
| `Extension.csproj`                | Executable project; no MTG dependency in the generated application         |
| `Extension.Generated.props`       | Regenerated identity, template dependencies, and protobuf build items      |
| `azd-template.json`               | Upstream template/protocol source revision                                 |
| `LICENSE.azd-template.txt`        | Upstream MIT license for the included template and contracts               |
| `build.ps1`, `build.sh`           | Self-contained platform builds compatible with `azd x pack`                |

`extension.yaml` is serialized as JSON, which is valid YAML and accepted by azd. This safely quotes author-provided text.

Run the generated executable before packaging:

```powershell
$extension = "samples/tagger/tsp-output/@typespec/azd-extension-csharp"
dotnet run --project "$extension/Extension.csproj" -- show
dotnet run --project "$extension/Extension.csproj" -- show --output json
dotnet run --project "$extension/Extension.csproj" -- metadata
```

The default output is:

```text
Hello from the Contoso Resource Tagger extension!
```

## Package and install locally

Run the sample script from the repository root with PowerShell 7.3 or later:

```powershell
pwsh -NoProfile -File packages\azd-extension-csharp\eng\Test-Sample.ps1
```

Or run `npm run test:sample` from this package. Install dependencies with `npm ci` first if needed.

The script builds the emitter/generator, generates the sample, uses `azd x build` for the host platform, creates and installs a local bundle, and exercises the greeting, help, and JSON output. It fails immediately on command errors. It downloads azd's developer extension but publishes nothing remotely.

The default isolated configuration is `test/output/manual-azd` within this package, with bundles in its `bundles` subdirectory. Existing azd extensions and sources are untouched. Use `-ConfigDirectory` to choose another dedicated configuration directory. The script restores the caller's environment variables and working directory on success or failure, and prints the command needed to select the isolated installation for subsequent manual use.

Rerun the script after editing TypeSpec; `--force` replaces the installed sample even when its version is unchanged. Neither compilation nor generation alone installs or publishes anything.

For manual cross-platform builds, invoke the generated `build.ps1` or `bash build.sh` with `EXTENSION_PLATFORM` set to `windows/amd64`, `windows/arm64`, `linux/amd64`, `linux/arm64`, `darwin/amd64`, or `darwin/arm64`. Leaving it unset builds all six targets. The scripts map azd's `amd64` architecture to .NET's `x64` RID.

## Architecture and ownership

The TypeScript library validates decorators and emits `tspCodeModel.json` directly from compiler types, without inventing HTTP operations or running the HTTP client emitter. The file contains MTG's namespace/type graph plus an `extension` section for identity and commands. Command results reference named constants in the shared graph rather than duplicating their values.

`AzdInputLibrary` overrides the virtual `InputNamespace` property to lazily load and cache an `AzdInputNamespace`. Its loader calls MTG's `TypeSpecSerialization.Deserialize` for the shared graph, then the azd input deserializer for the extension section. The custom reader binds command results to those same `InputLiteralType` instances, preserving shared type identity without changing MTG's serializer or creating a second type graph.

The MEF-exported `AzdExtensionGenerator : CodeModelGenerator` consumes `InputExtension` and `InputCommand` through that input library. It does not read or deserialize the code model itself. The generator uses MTG's `TypeFactory`, `TypeProvider`, method/expression model, writers, and Roslyn pipeline to generate the entry point and command classes. The application uses System.CommandLine, dependency injection, and azd's protobuf/gRPC client pattern; it does not load the generator at runtime.

There is **no new shared runtime or RPC server**. azd already owns the host protocol. As in the official C# starter, `Program` registers `AzdClient` as a singleton and registers command classes as transients, then adds each command's `Build()` result to the root. The generated project compiles upstream protobuf definitions into clients. `AzdClient` reads `AZD_SERVER` and `AZD_ACCESS_TOKEN` when resolved, and attaches authentication to unary and streaming calls. The greeting and metadata do not resolve it, so they also work outside an azd invocation.

`src/Generated`, `proto`, `extension.yaml`, `Extension.Generated.props`, and template provenance/license files are generator-owned and replaced on regeneration. Do not edit them. The project, build scripts, `.gitignore`, and changelog are scaffolded only when absent, preserving subsequent author edits. Identity/version changes flow through the generated properties without overwriting those files. Other authored C# files are preserved. The output directory should be dedicated to this extension.

## azd C# template baseline

The output follows the [official .NET template](https://github.com/Azure/azure-dev/tree/d5ed6cd86f45dc7c44da353d6bc4f5a3ef20d873/cli/azd/extensions/microsoft.azd.extensions/internal/resources/languages/dotnet), pinned together with its flattened `languages/proto` contracts at commit `d5ed6cd86f45dc7c44da353d6bc4f5a3ef20d873`. Snapshots are embedded under `generator/assets/upstream`, so generation does not fetch files from GitHub. `azd-template.json` records that provenance in each output.

The template's architecture is the target, with these explicit differences:

| Area                                   | Reason for the difference                                                                                                                                                                                                                                                                                   |
| -------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Generated source directory             | The template's `Program.cs`, `commands`, `AzdClient.cs`, and `EventManager.cs` layout is retained beneath `src/Generated` so regeneration can safely remove obsolete commands without deleting authored code.                                                                                               |
| Project file and generated properties  | The existing `Extension.csproj` name is retained; generated properties supply the template's dependency families and protobuf items while preserving project edits. azd builds through its scripts, not a required project filename.                                                                        |
| .NET and System.CommandLine versions   | The prototype retains .NET 10 and stable System.CommandLine 2.0 rather than downgrading to the template's .NET 9 and prerelease CLI package. `Subcommands.Add`/`SetAction` are the corresponding stable APIs.                                                                                               |
| Standard flags and metadata            | `CommandOptions` preserves azd-compatible flags and the existing JSON/none output behavior; a generated `MetadataCommand` exposes the declared command tree.                                                                                                                                                |
| Authored commands versus demo commands | The stock `context`, `prompt`, and `listen` demonstration bodies are not injected as undeclared behavior. TypeSpec remains the command source of truth; lifecycle and RPC behavior bindings remain future authoring features. The client and event infrastructure are included.                             |
| Missing host context                   | The default `AzdClient` constructor explicitly rejects missing host environment values, avoiding nullable warnings and making accidental standalone host calls actionable.                                                                                                                                  |
| Platform publishing                    | Build scripts preserve azd's artifact names and platform environment conventions, map `amd64` to .NET `x64`, isolate publish directories, and fail on errors. They do not enable unvalidated trimming. Bare local builds remain framework-dependent; platform publishing is self-contained and single-file. |

This is template-aligned output, not byte-for-byte copying of the starter's demo application. The generated `AzdClient` and `EventManager` follow the upstream helper implementation, with the missing-context checks described above. The upstream MIT license is included in every generated project and embedded in the executable so it travels with binary bundles.

## Validation

```powershell
npm run build
npm test
npm run test:e2e
npm run format:check
npm run lint
```

The end-to-end suite generates and builds an executable, checks exact greeting/JSON output, help, metadata, input errors, escaping, deterministic regeneration, stale-file cleanup, and preservation of authored files. It also checks `--no-emit`, invalid contracts, and missing .NET.

`npm test` includes emitter tests and .NET tests covering input-library loading/caching, shared reference identity, malformed models, and authenticated unary/event-stream calls against a local gRPC host. The end-to-end checks also cover the template-shaped layout, DI registrations, and protobuf client compilation.

To additionally publish the host-platform executable, pack a real bundle, install it, and invoke it through azd in a temporary isolated configuration:

```powershell
$env:AZD_PROTOTYPE_E2E = "1"
npm run test:e2e
```

This check invokes `eng/Test-Sample.ps1` from another working directory and verifies that it restores the caller's environment. It needs azd, network access to install the developer extension, PowerShell 7.3 or later, and `bash` on Linux/macOS. It does not publish to any remote registry or use Azure credentials.

With azd 1.37.2 on Windows, keep `AZD_CONFIG_DIR` in a path without spaces: the upstream developer-extension launcher fails to quote that executable path. The emitter and generated executable support paths with spaces independently of that host limitation.

## Next slices, not implemented

The north star remains minimal-code extension authoring with typed compositions and custom C# only for unusual logic. The prototype now emits azd's C# client/event infrastructure, but it does **not** yet implement TypeSpec authoring for parameters, SDK or azd RPC bindings, multi-step compositions, lifecycle hooks, providers, configuration schemas, MCP, or custom-handler escape hatches.

When compositions are added, the agreed default is fail-fast: report the failed step, skip later steps, preserve completed side effects, and perform no implicit retries or rollback. The current greeting has no such side effects.
