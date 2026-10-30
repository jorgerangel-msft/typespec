import "../dist/src/index.js";

namespace TypeSpec.Azd;

/** Metadata for an independently installable azd extension. */
model ExtensionOptions {
  id: string;
  commandNamespace: string;
  version: string;
  displayName: string;
}

/**
 * Declare an azd extension. Its documentation becomes the extension description.
 * @param options Extension identity and command namespace.
 */
extern dec extension(target: Reflection.Namespace, options: valueof ExtensionOptions);

/**
 * Expose an operation as an azd command.
 *
 * This prototype supports parameterless operations returning a string literal.
 * The literal is the implementation: the generated command prints it.
 * @param name Optional CLI name; defaults to the operation name.
 */
extern dec command(target: Reflection.Operation, name?: valueof string);
