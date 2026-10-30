// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.TypeSpec.Generator.Input;

namespace TypeSpec.Azd.Generator.Input;

public sealed record InputExtension(
    int SchemaVersion,
    string Id,
    string Namespace,
    string Version,
    string DisplayName,
    string Description,
    IReadOnlyList<InputCommand> Commands)
{
    public string EntryPoint => Id.Replace('.', '-');
}

public sealed record InputCommand(string Name, string Description, InputLiteralType Result);
