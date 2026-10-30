import { getDoc, NoTarget, type Namespace, type Operation, type Program } from "@typespec/compiler";
import { getCommands, getExtensions } from "./decorators.js";
import { reportDiagnostic } from "./lib.js";

export interface ExtensionModel {
  schemaVersion: 1;
  id: string;
  namespace: string;
  version: string;
  displayName: string;
  description: string;
  commands: { name: string; description: string; message: string }[];
}

export interface AzdCodeModel {
  name: string;
  apiVersions: string[];
  constants: {
    kind: "constant";
    name: string;
    namespace: string;
    valueType: { kind: "string"; name: "string" };
    value: string;
  }[];
  enums: [];
  models: [];
  clients: [];
  extension: Omit<ExtensionModel, "commands"> & {
    commands: { name: string; description: string; result: string }[];
  };
}

export function createCodeModel(extension: ExtensionModel): AzdCodeModel {
  const namespace = "TypeSpec.Azd.Generated";
  return {
    name: namespace,
    apiVersions: [],
    constants: extension.commands.map((command) => ({
      kind: "constant",
      name: `${command.name}.result`,
      namespace,
      valueType: { kind: "string", name: "string" },
      value: command.message,
    })),
    enums: [],
    models: [],
    clients: [],
    extension: {
      ...extension,
      commands: extension.commands.map((command) => ({
        name: command.name,
        description: command.description,
        result: `${command.name}.result`,
      })),
    },
  };
}

const namePattern = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const versionPattern =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?$/;

export function getExtensionModel(program: Program): ExtensionModel | undefined {
  const extensions = [...getExtensions(program)];
  if (extensions.length !== 1) {
    reportDiagnostic(program, { code: "extension-count", target: extensions[1]?.[0] ?? NoTarget });
    return undefined;
  }
  const [namespace, options] = extensions[0];
  if (
    !/^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$/.test(options.id) ||
    !namePattern.test(options.commandNamespace) ||
    !versionPattern.test(options.version) ||
    options.displayName.trim().length === 0
  ) {
    reportDiagnostic(program, {
      code: "invalid-extension",
      target: namespace,
      format: {
        detail:
          "Use a lowercase dot/hyphen-separated id, a lowercase kebab-case commandNamespace, a semantic version, and a nonempty displayName.",
      },
    });
  }

  const commands: ExtensionModel["commands"] = [];
  const names = new Set<string>();
  const operations = getOperations(namespace);
  const declaredCommands = getCommands(program);
  for (const [operation, name] of declaredCommands) {
    if (!operations.includes(operation)) {
      reportDiagnostic(program, { code: "command-outside-extension", target: operation });
      continue;
    }
    if (!namePattern.test(name) || name === "metadata" || name === "help") {
      reportDiagnostic(program, { code: "invalid-command", target: operation });
    }
    if (names.has(name)) {
      reportDiagnostic(program, { code: "duplicate-command", target: operation });
    }
    names.add(name);
    if (operation.parameters.properties.size !== 0 || operation.returnType.kind !== "String") {
      reportDiagnostic(program, { code: "unsupported-operation", target: operation });
      continue;
    }
    commands.push({
      name,
      description: getDoc(program, operation) ?? name,
      message: operation.returnType.value,
    });
  }
  if (commands.length === 0 || operations.some((op) => !declaredCommands.has(op))) {
    reportDiagnostic(program, { code: "missing-command", target: namespace });
  }
  if (program.hasError()) return undefined;

  return {
    schemaVersion: 1,
    id: options.id,
    namespace: options.commandNamespace,
    version: options.version,
    displayName: options.displayName,
    description: getDoc(program, namespace) ?? options.displayName,
    commands: commands.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)),
  };
}

function getOperations(namespace: Namespace): Operation[] {
  return [
    ...namespace.operations.values(),
    ...[...namespace.interfaces.values()].flatMap((x) => [...x.operations.values()]),
    ...[...namespace.namespaces.values()].flatMap(getOperations),
  ];
}

export function $onValidate(program: Program): void {
  getExtensionModel(program);
}
