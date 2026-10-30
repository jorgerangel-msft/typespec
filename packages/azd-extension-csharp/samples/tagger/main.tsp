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
