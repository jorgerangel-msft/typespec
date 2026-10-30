// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;

namespace TypeSpec.Azd.Generator;

internal abstract class SupportTypeProvider(string name, string ns = "Microsoft.Azd") : TypeProvider
{
    protected override string BuildName() => name;
    protected override string BuildNamespace() => ns;
    protected override string BuildRelativeFilePath() => Path.Combine("src", "Generated", $"{Name}.cs");
    protected override TypeSignatureModifiers BuildDeclarationModifiers() => TypeSignatureModifiers.Public | TypeSignatureModifiers.Sealed;

    protected MethodProvider Method(string name, CSharpType? result, IReadOnlyList<ParameterProvider> parameters,
        MethodBodyStatement body, MethodSignatureModifiers modifiers = MethodSignatureModifiers.Public)
        => new(new MethodSignature(name, null, modifiers, result, null, parameters), body, this);
}
