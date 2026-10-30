// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.TypeSpec.Generator.Input;

namespace TypeSpec.Azd.Generator.Input.Serialization;

internal static class AzdInputSerialization
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static InputExtension Deserialize(JsonElement element, InputNamespace input)
    {
        var data = element.Deserialize<ExtensionData>(s_options)
            ?? throw new JsonException("The extension section cannot be null.");
        if (data.SchemaVersion != 1)
        {
            throw new JsonException($"Unsupported extension IR version {data.SchemaVersion}.");
        }
        if (data.Commands.Length == 0)
        {
            throw new JsonException("The extension section must contain at least one command.");
        }

        var constants = new Dictionary<string, InputLiteralType>(StringComparer.Ordinal);
        foreach (var constant in input.Constants)
        {
            if (!constants.TryAdd(constant.Name, constant))
            {
                throw new JsonException($"Duplicate code-model constant '{constant.Name}'.");
            }
        }
        var commands = new List<InputCommand>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in data.Commands)
        {
            if (command is null)
            {
                throw new JsonException("Extension commands cannot be null.");
            }
            if (!names.Add(command.Name))
            {
                throw new JsonException($"Duplicate extension command '{command.Name}'.");
            }
            if (!constants.TryGetValue(command.Result, out var result))
            {
                throw new JsonException($"Command '{command.Name}' references unknown result constant '{command.Result}'.");
            }
            if (result.ValueType.Kind != InputPrimitiveTypeKind.String)
            {
                throw new JsonException($"Command '{command.Name}' must reference a string literal result.");
            }
            commands.Add(new InputCommand(command.Name, command.Description, result));
        }
        return new InputExtension(data.SchemaVersion, data.Id, data.Namespace, data.Version,
            data.DisplayName, data.Description, commands);
    }

    private sealed record ExtensionData(
        int SchemaVersion,
        string Id,
        string Namespace,
        string Version,
        string DisplayName,
        string Description,
        CommandData[] Commands);

    private sealed record CommandData(string Name, string Description, string Result);
}
