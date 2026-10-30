import { createTypeSpecLibrary, paramMessage, type JSONSchemaType } from "@typespec/compiler";

export interface AzdEmitterOptions {
  "save-inputs"?: boolean;
}

const emitterOptions: JSONSchemaType<AzdEmitterOptions> = {
  type: "object",
  additionalProperties: false,
  properties: {
    "save-inputs": {
      type: "boolean",
      nullable: true,
      default: true,
      description:
        "Keep tspCodeModel.json and Configuration.json after successful generation. Failed-generation inputs are always retained.",
    },
  },
};

export const $lib = createTypeSpecLibrary({
  name: "@typespec/azd-extension-csharp",
  emitter: { options: emitterOptions },
  diagnostics: {
    "extension-count": {
      severity: "error",
      messages: { default: "Declare exactly one namespace with @extension per compilation." },
    },
    "invalid-extension": {
      severity: "error",
      messages: { default: paramMessage`${"detail"}` },
    },
    "missing-command": {
      severity: "error",
      messages: {
        default: "An extension must expose commands, and every operation in it must have @command.",
      },
    },
    "unsupported-operation": {
      severity: "error",
      messages: {
        default:
          "This prototype supports only parameterless commands returning a string literal. Declare the literal behavior instead of an unimplemented result type.",
      },
    },
    "invalid-command": {
      severity: "error",
      messages: {
        default: "Command names must be lowercase kebab-case. 'help' and 'metadata' are reserved.",
      },
    },
    "duplicate-command": {
      severity: "error",
      messages: { default: "Each command must have a unique CLI name within the extension." },
    },
    "command-outside-extension": {
      severity: "error",
      messages: { default: "@command must be declared inside the @extension namespace." },
    },
    "generation-failed": {
      severity: "error",
      messages: { default: paramMessage`The MTG azd generator failed: ${"detail"}` },
    },
  },
});

export const { reportDiagnostic, createStateSymbol } = $lib;
