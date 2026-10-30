import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const compiler = join(root, "node_modules", "@typespec", "compiler", "cmd", "tsp.js");
const greeting = "Hello from the Contoso Resource Tagger extension!";
const sample = join(root, "samples", "tagger", "main.tsp");

function run(command, args, options = {}) {
  return execFileSync(command, args, {
    cwd: root,
    encoding: "utf8",
    stdio: "pipe",
    timeout: 180_000,
    ...options,
  });
}

async function workspace(t, prefix = "extension prototype-") {
  const parent = join(root, "test", "output");
  await mkdir(parent, { recursive: true });
  const directory = await mkdtemp(join(parent, prefix));
  t.after(() => rm(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }));
  return directory;
}

function generate(source, directory, extraArgs = [], options = {}) {
  return run(
    process.execPath,
    [
      compiler,
      "compile",
      source,
      "--emit",
      "@typespec/azd-extension-csharp",
      "--output-dir",
      directory,
      ...extraArgs,
    ],
    options,
  );
}

function expectDiagnostic(action, code) {
  assert.throws(action, (error) => {
    assert.match(error.stdout, new RegExp(code));
    return true;
  });
}

function normalizeSnapshot(text) {
  return text.replace(/^\uFEFF/, "").replace(/\r\n/g, "\n");
}

test(
  "generates, builds, and regenerates a functional zero-code extension",
  { timeout: 180_000 },
  async (t) => {
    const directory = await workspace(t);
    generate(sample, directory);
    const output = join(directory, "@typespec", "azd-extension-csharp");
    const codeModel = JSON.parse(await readFile(join(output, "tspCodeModel.json"), "utf8"));
    assert.equal(codeModel.name, "TypeSpec.Azd.Generated");
    assert.equal(codeModel.extension.id, "contoso.azd.tagger");
    assert.deepEqual(codeModel.constants, [
      {
        kind: "constant",
        name: "show.result",
        namespace: codeModel.name,
        valueType: { kind: "string", name: "string" },
        value: greeting,
      },
    ]);
    assert.equal(codeModel.extension.commands[0].result, codeModel.constants[0].name);
    assert.ok(!("message" in codeModel.extension.commands[0]));
    const generatedDirectory = join(output, "src", "Generated");
    const program = await readFile(join(generatedDirectory, "Program.cs"), "utf8");
    assert.match(program, /new ServiceCollection/);
    assert.match(program, /AddSingleton<AzdClient>/);
    assert.match(program, /AddTransient<ShowCommand>/);
    assert.match(program, /GetRequiredService<ShowCommand>/);
    const command = await readFile(join(generatedDirectory, "commands", "ShowCommand.cs"), "utf8");
    assert.match(command, /class ShowCommand/);
    assert.match(command, /Command Build\(\)/);
    assert.match(
      await readFile(join(generatedDirectory, "AzdClient.cs"), "utf8"),
      /AZD_ACCESS_TOKEN/,
    );
    assert.match(
      await readFile(join(generatedDirectory, "EventManager.cs"), "utf8"),
      /EventStream/,
    );
    assert.match(
      await readFile(join(output, "proto", "project.proto"), "utf8"),
      /csharp_namespace = "Microsoft.Azd"/,
    );
    assert.match(
      await readFile(join(output, "Extension.Generated.props"), "utf8"),
      /GrpcServices="Client"/,
    );
    assert.match(
      await readFile(join(output, "Extension.Generated.props"), "utf8"),
      /LogicalName="TypeSpec.Azd.Template.License"/,
    );
    const baseline = JSON.parse(await readFile(join(output, "azd-template.json"), "utf8"));
    assert.equal(baseline.commit, "d5ed6cd86f45dc7c44da353d6bc4f5a3ef20d873");
    const protoFiles = [
      "deployment.proto",
      "environment.proto",
      "errors.proto",
      "event.proto",
      "models.proto",
      "project.proto",
      "prompt.proto",
      "user_config.proto",
      "workflow.proto",
    ];
    assert.deepEqual((await readdir(join(output, "proto"))).sort(), protoFiles);
    for (const name of protoFiles) {
      assert.equal(
        await readFile(join(output, "proto", name), "utf8"),
        normalizeSnapshot(
          await readFile(join(root, "generator", "assets", "upstream", "proto", name), "utf8"),
        ),
      );
    }
    for (const name of ["AzdClient.cs", "EventManager.cs"]) {
      assert.equal(
        await readFile(join(generatedDirectory, name), "utf8"),
        normalizeSnapshot(
          await readFile(join(root, "generator", "assets", "upstream", "dotnet", name), "utf8"),
        ),
      );
    }
    assert.match(await readFile(join(output, "LICENSE.azd-template.txt"), "utf8"), /MIT License/);
    assert.match(await readFile(join(output, ".gitignore"), "utf8"), /bin\//);
    assert.match(await readFile(join(output, "CHANGELOG.md"), "utf8"), /0\.1\.0/);
    const project = join(output, "Extension.csproj");
    let app = join(output, "bin", "Debug", "net10.0", "contoso-azd-tagger.dll");
    run("dotnet", ["build", project, "--nologo", "-v:q"]);
    const invoke = (...args) => run("dotnet", [app, ...args]);

    await t.test("matches the quickstart and supports standard output flags", () => {
      assert.equal(invoke("show").trim(), greeting);
      assert.equal(JSON.parse(invoke("show", "--output", "json")), greeting);
      assert.equal(invoke("show", "-o", "none"), "");
      assert.equal(invoke("--debug", "--no-prompt", "-e", "test", "show").trim(), greeting);
      assert.match(invoke("show", "--help"), /Displays a greeting/);
    });

    await t.test("manifest and metadata agree with the authoring contract", async () => {
      const manifest = JSON.parse(await readFile(join(output, "extension.yaml"), "utf8"));
      assert.equal(manifest.id, "contoso.azd.tagger");
      assert.equal(manifest.namespace, "tagger");
      assert.deepEqual(manifest.capabilities, ["custom-commands", "metadata"]);
      const metadata = JSON.parse(invoke("metadata"));
      assert.equal(metadata.schemaVersion, "1.0");
      assert.equal(metadata.id, manifest.id);
      assert.deepEqual(metadata.commands[0].name, ["tagger"]);
      assert.deepEqual(metadata.commands[0].subcommands[0].name, ["tagger", "show"]);
    });

    await t.test("invalid input fails without success output", () => {
      for (const args of [
        ["unknown"],
        ["show", "extra"],
        ["show", "--output", "invalid"],
        ["show", "--cwd", join(directory, "missing")],
      ]) {
        const result = spawnSync("dotnet", [app, ...args], { encoding: "utf8" });
        assert.notEqual(result.status, 0);
        assert.ok(!result.stdout.includes(greeting));
        assert.ok(result.stderr.length > 0);
      }
    });

    await t.test("regeneration updates behavior and preserves authored files", async () => {
      const generated = join(output, "src", "Generated", "Program.cs");
      const first = await readFile(generated, "utf8");
      generate(sample, directory);
      assert.equal(await readFile(generated, "utf8"), first);

      const projectContent = await readFile(project, "utf8");
      const custom = join(output, "src", "Custom.cs");
      await writeFile(custom, "// Author-owned code.\ninternal static class AuthorFile {}\n");
      await writeFile(project, projectContent + "\n<!-- Author-owned project edit. -->\n");
      await writeFile(
        join(output, "src", "Generated", "Stale.cs"),
        "// Obsolete generated source.\n",
      );
      const message = 'Quotes " and backslashes \\\n"); throw new Exception("not code"); //';
      const source = (await readFile(sample, "utf8"))
        .replace(/@command\s+op show\(\)/, '@command("hello")\nop show()')
        .replace(JSON.stringify(greeting), JSON.stringify(message))
        .replace("contoso.azd.tagger", "contoso.azd.other")
        .replace("0.1.0", "0.2.0");
      const input = join(directory, "main.tsp");
      await writeFile(input, source);
      generate(input, directory);
      assert.equal(
        await readFile(custom, "utf8"),
        "// Author-owned code.\ninternal static class AuthorFile {}\n",
      );
      assert.equal(
        await readFile(project, "utf8"),
        projectContent + "\n<!-- Author-owned project edit. -->\n",
      );
      assert.ok(!(await readdir(join(output, "src", "Generated"))).includes("Stale.cs"));
      assert.deepEqual(await readdir(join(output, "src", "Generated", "commands")), [
        "HelloCommand.cs",
        "MetadataCommand.cs",
      ]);
      run("dotnet", ["build", project, "--nologo", "-v:q"]);
      app = join(output, "bin", "Debug", "net10.0", "contoso-azd-other.dll");
      assert.equal(invoke("hello").trimEnd(), message);
      assert.match(invoke("--version"), /^0\.2\.0/);
      assert.equal(JSON.parse(invoke("metadata")).id, "contoso.azd.other");
      assert.deepEqual(JSON.parse(invoke("metadata")).commands[0].subcommands[0].name, [
        "tagger",
        "hello",
      ]);
      assert.notEqual(spawnSync("dotnet", [app, "show"]).status, 0);
    });
  },
);

test("save-inputs controls retention without deleting failed-generation inputs", async (t) => {
  const directory = await workspace(t);
  const output = join(directory, "@typespec", "azd-extension-csharp");
  const option = "@typespec/azd-extension-csharp.save-inputs";
  generate(sample, directory, ["--option", `${option}=false`]);
  for (const name of ["tspCodeModel.json", "Configuration.json"]) {
    await assert.rejects(readFile(join(output, name)), { code: "ENOENT" });
  }
  generate(sample, directory, ["--option", `${option}=true`]);
  assert.equal(
    JSON.parse(await readFile(join(output, "tspCodeModel.json"), "utf8")).extension.id,
    "contoso.azd.tagger",
  );
  assert.equal(
    JSON.parse(await readFile(join(output, "Configuration.json"), "utf8"))["package-name"],
    "TypeSpec.Azd.Generated",
  );
  expectDiagnostic(
    () =>
      generate(sample, directory, ["--option", `${option}=false`], {
        env: { ...process.env, PATH: "" },
      }),
    "generation-failed",
  );
  assert.ok((await readdir(output)).includes("tspCodeModel.json"));
  assert.ok((await readdir(output)).includes("Configuration.json"));
});

test("noEmit and invalid contracts never invoke the generator", async (t) => {
  const directory = await workspace(t);
  generate(sample, directory, ["--no-emit"], { env: { ...process.env, PATH: "" } });
  assert.deepEqual(await readdir(directory), []);
  const input = join(directory, "main.tsp");
  await writeFile(
    input,
    (await readFile(sample, "utf8")).replace(JSON.stringify(greeting), "string"),
  );
  expectDiagnostic(
    () => generate(input, directory, [], { env: { ...process.env, PATH: "" } }),
    "unsupported-operation",
  );
  assert.deepEqual(await readdir(directory), ["main.tsp"]);
});

test("missing .NET is an actionable compiler diagnostic", async (t) => {
  const directory = await workspace(t);
  expectDiagnostic(
    () => generate(sample, directory, [], { env: { ...process.env, PATH: "" } }),
    "generation-failed",
  );
});

test(
  "packages and invokes the sample through real azd in an isolated configuration",
  {
    skip:
      process.env.AZD_PROTOTYPE_E2E !== "1" &&
      "Set AZD_PROTOTYPE_E2E=1 to enable azd installation and packaging.",
    timeout: 600_000,
  },
  async (t) => {
    const directory = await workspace(t, "azd-prototype-");
    const env = {
      ...process.env,
      AZD_CONFIG_DIR: join(directory, "azd-config"),
      AZD_NON_INTERACTIVE: "true",
      AZURE_DEV_COLLECT_TELEMETRY: "no",
    };
    const scriptOutput = run(
      "pwsh",
      [
        "-NoProfile",
        "-Command",
        `
      $ErrorActionPreference = 'Stop'
      $before = (Get-Location).Path
      $config = $env:AZD_CONFIG_DIR
      $nonInteractive = $env:AZD_NON_INTERACTIVE
      $telemetry = $env:AZURE_DEV_COLLECT_TELEMETRY
      & $env:AZD_PROTOTYPE_SCRIPT -ConfigDirectory $env:AZD_PROTOTYPE_CONFIG
      & $env:AZD_PROTOTYPE_SCRIPT -ConfigDirectory $env:AZD_PROTOTYPE_CONFIG
      if ((Get-Location).Path -ne $before -or $env:AZD_CONFIG_DIR -ne $config -or
          $env:AZD_NON_INTERACTIVE -ne $nonInteractive -or $env:AZURE_DEV_COLLECT_TELEMETRY -ne $telemetry) {
        throw 'The sample runner must restore its caller environment and working directory.'
      }
      function npm { & (Get-Command pwsh).Source -NoProfile -Command 'exit 17' }
      $failed = $false
      try {
        & $env:AZD_PROTOTYPE_SCRIPT -ConfigDirectory $env:AZD_PROTOTYPE_CONFIG
      }
      catch {
        if ($_.Exception.Message -notmatch '17') { throw }
        $failed = $true
      }
      if (-not $failed) { throw 'The sample runner must stop on a native command failure.' }
      if ((Get-Location).Path -ne $before -or $env:AZD_CONFIG_DIR -ne $config -or
          $env:AZD_NON_INTERACTIVE -ne $nonInteractive -or $env:AZURE_DEV_COLLECT_TELEMETRY -ne $telemetry) {
        throw 'The sample runner must restore caller state after a failure.'
      }
    `,
      ],
      {
        cwd: directory,
        env: {
          ...env,
          AZD_CONFIG_DIR: join(directory, "caller-config"),
          AZD_NON_INTERACTIVE: "false",
          AZURE_DEV_COLLECT_TELEMETRY: "yes",
          AZD_PROTOTYPE_SCRIPT: join(root, "eng", "Test-Sample.ps1"),
          AZD_PROTOTYPE_CONFIG: env.AZD_CONFIG_DIR,
        },
      },
    );
    assert.ok(scriptOutput.includes(greeting));
    const azd = (...args) => run("azd", args, { env });
    const artifacts = join(env.AZD_CONFIG_DIR, "bundles");
    const bundle = (await readdir(artifacts)).find((name) => name.endsWith(".zip"));
    assert.ok(bundle, "azd x pack must produce an installable bundle.");
    assert.equal(azd("tagger", "show").trim(), greeting);
    assert.equal(JSON.parse(azd("tagger", "show", "--output", "json")), greeting);
    assert.match(azd("tagger", "show", "--help"), /Displays a greeting/);
  },
);
