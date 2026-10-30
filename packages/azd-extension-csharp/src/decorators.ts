import {
  validateDecoratorUniqueOnNode,
  type DecoratorContext,
  type Namespace,
  type Operation,
  type Program,
} from "@typespec/compiler";
import { createStateSymbol } from "./lib.js";

export interface ExtensionOptions {
  id: string;
  commandNamespace: string;
  version: string;
  displayName: string;
}

const extensionKey = createStateSymbol("extension");
const commandKey = createStateSymbol("command");

export function getExtensions(program: Program): Map<Namespace, ExtensionOptions> {
  return program.stateMap(extensionKey) as Map<Namespace, ExtensionOptions>;
}

export function getCommands(program: Program): Map<Operation, string> {
  return program.stateMap(commandKey) as Map<Operation, string>;
}

export function $extension(
  context: DecoratorContext,
  target: Namespace,
  options: ExtensionOptions,
) {
  if (validateDecoratorUniqueOnNode(context, target, $extension)) {
    getExtensions(context.program).set(target, options);
  }
}

export function $command(context: DecoratorContext, target: Operation, name?: string) {
  if (validateDecoratorUniqueOnNode(context, target, $command)) {
    getCommands(context.program).set(target, name ?? target.name);
  }
}

export const $decorators = {
  "TypeSpec.Azd": {
    extension: $extension,
    command: $command,
  },
};
