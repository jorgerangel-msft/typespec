// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.TypeSpec.Generator.Input;

namespace TypeSpec.Azd.Generator.Input;

public sealed class AzdInputNamespace(InputNamespace input, InputExtension extension)
    : InputNamespace(input.Name, input.ApiVersions, input.Constants, input.Enums, input.Models, input.RootClients, input.Auth)
{
    public InputExtension Extension { get; } = extension;
}
