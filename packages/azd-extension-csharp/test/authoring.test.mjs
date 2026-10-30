import assert from "node:assert/strict";
import { test } from "node:test";
import { createTester } from "@typespec/compiler/testing";
import { getExtensionModel } from "../dist/src/model.js";

const Tester = createTester(import.meta.dirname + "/..", {
  libraries: ["@typespec/azd-extension-csharp"],
})
  .importLibraries()
  .using("TypeSpec.Azd");

const header = `
@extension(#{
  id: "contoso.azd.tagger",
  commandNamespace: "tagger",
  version: "0.1.0",
  displayName: "Contoso Resource Tagger"
})
@doc("Standardize and report Azure resource tags for an azd project.")
namespace Contoso.ResourceTagger;
`;

test("rejects multiple extension declarations", async () => {
  const first = header.replace("namespace Contoso.ResourceTagger;", "namespace First {");
  const second = header.replace("namespace Contoso.ResourceTagger;", "namespace Second {");
  const diagnostics = await Tester.diagnose(
    `${first} @command op show(): "first"; } ${second} @command op show(): "second"; }`,
  );
  assert.ok(diagnostics.some((x) => x.code === "@typespec/azd-extension-csharp/extension-count"));
});

test("the quickstart has an executable behavior without authored C#", async () => {
  const { program } = await Tester.compile(`${header}
    @doc("Displays a greeting from the Contoso Resource Tagger extension.")
    @command
    op show(): "Hello from the Contoso Resource Tagger extension!";
  `);
  assert.deepEqual(getExtensionModel(program), {
    schemaVersion: 1,
    id: "contoso.azd.tagger",
    namespace: "tagger",
    version: "0.1.0",
    displayName: "Contoso Resource Tagger",
    description: "Standardize and report Azure resource tags for an azd project.",
    commands: [
      {
        name: "show",
        description: "Displays a greeting from the Contoso Resource Tagger extension.",
        message: "Hello from the Contoso Resource Tagger extension!",
      },
    ],
  });
});

test("commands have explicit CLI names and deterministic order", async () => {
  const { program } = await Tester.compile(`${header}
    @command("z-last") op last(): "last";
    @command("a-first") op first(): "first";
  `);
  assert.deepEqual(
    getExtensionModel(program).commands.map((x) => x.name),
    ["a-first", "z-last"],
  );
});

for (const [name, source, code] of [
  ["missing extension", '@command op show(): "hello";', "extension-count"],
  ["missing commands", header, "missing-command"],
  ["missing behavior", `${header} @command op show(): string;`, "unsupported-operation"],
  ["void result", `${header} @command op show(): void;`, "unsupported-operation"],
  ["parameters", `${header} @command op show(name: string): "hello";`, "unsupported-operation"],
  [
    "duplicate CLI names",
    `${header} @command("show") op first(): "a"; @command("show") op second(): "b";`,
    "duplicate-command",
  ],
  ["reserved command", `${header} @command("metadata") op show(): "hello";`, "invalid-command"],
  ["invalid CLI name", `${header} @command("../show") op show(): "hello";`, "invalid-command"],
  [
    "invalid extension id",
    header.replace("contoso.azd.tagger", "../tagger") + '@command op show(): "hello";',
    "invalid-extension",
  ],
  [
    "invalid version",
    header.replace("0.1.0", "latest") + '@command op show(): "hello";',
    "invalid-extension",
  ],
  ["unbound operation", `${header} op show(): "hello";`, "missing-command"],
  [
    "command outside extension",
    `${header.replace("namespace Contoso.ResourceTagger;", "namespace Contoso.ResourceTagger {")} @command op show(): "hello"; } namespace Elsewhere { @command op other(): "x"; }`,
    "command-outside-extension",
  ],
]) {
  test(`rejects ${name} instead of generating partial behavior`, async () => {
    const diagnostics = await Tester.diagnose(source);
    assert.ok(
      diagnostics.some((x) => x.code === `@typespec/azd-extension-csharp/${code}`),
      diagnostics.map((x) => `${x.code}: ${x.message}`).join("\n"),
    );
  });
}

test("noEmit validates without invoking dotnet", async () => {
  await Tester.compile(`${header} @command op show(): "hello";`, { noEmit: true });
});
