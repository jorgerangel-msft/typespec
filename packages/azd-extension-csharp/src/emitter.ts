import { emitFile, NoTarget, resolvePath, type EmitContext } from "@typespec/compiler";
import { execFile } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { reportDiagnostic, type AzdEmitterOptions } from "./lib.js";
import { createCodeModel, getExtensionModel } from "./model.js";

const execute = promisify(execFile);

export async function $onEmit(context: EmitContext<AzdEmitterOptions>): Promise<void> {
  if (context.program.compilerOptions.noEmit || context.program.hasError()) return;
  const model = getExtensionModel(context.program);
  if (!model) return;

  await emitFile(context.program, {
    path: resolvePath(context.emitterOutputDir, "tspCodeModel.json"),
    content: JSON.stringify(createCodeModel(model), null, 2) + "\n",
  });
  await emitFile(context.program, {
    path: resolvePath(context.emitterOutputDir, "Configuration.json"),
    content:
      JSON.stringify(
        {
          "package-name": "TypeSpec.Azd.Generated",
          "unreferenced-types-handling": "keepAll",
          "disable-xml-docs": true,
        },
        null,
        2,
      ) + "\n",
  });

  const generator = fileURLToPath(
    new URL("../generator/Microsoft.TypeSpec.Generator.dll", import.meta.url),
  );
  try {
    const { stdout } = await execute(
      "dotnet",
      [generator, context.emitterOutputDir, "-g", "AzdExtensionGenerator"],
      {
        maxBuffer: 10 * 1024 * 1024,
      },
    );
    for (const line of stdout.split(/\r?\n/).filter((line) => line.length > 0)) {
      const notification: unknown = JSON.parse(line);
      if (!isRecord(notification) || !isRecord(notification.params)) {
        throw new Error("Invalid MTG notification.");
      }
      const params = notification.params;
      if (notification.method === "diagnostic") {
        if (
          typeof params.code !== "string" ||
          typeof params.message !== "string" ||
          (params.severity !== "error" && params.severity !== "warning")
        ) {
          throw new Error("Invalid MTG diagnostic.");
        }
        context.program.reportDiagnostic({
          code: params.code,
          severity: params.severity,
          message: params.message,
          target: NoTarget,
        });
      } else if (notification.method === "trace" && typeof params.message === "string") {
        context.program.trace("@typespec/azd-extension-csharp.generator", params.message);
      }
    }
  } catch (error) {
    if (!(error instanceof Error)) throw error;
    const stderr = "stderr" in error && typeof error.stderr === "string" ? error.stderr : "";
    reportDiagnostic(context.program, {
      code: "generation-failed",
      target: NoTarget,
      format: {
        detail: `${stderr.trim() || error.message}\nRun npm run build in @typespec/azd-extension-csharp and ensure .NET 10 is installed.`,
      },
    });
  }

  if (context.options["save-inputs"] === false && !context.program.hasError()) {
    await Promise.all(
      ["tspCodeModel.json", "Configuration.json"].map((name) =>
        context.program.host.rm(resolvePath(context.emitterOutputDir, name)),
      ),
    );
  }

  function isRecord(value: unknown): value is Record<string, unknown> {
    return value !== null && typeof value === "object" && !Array.isArray(value);
  }
}
