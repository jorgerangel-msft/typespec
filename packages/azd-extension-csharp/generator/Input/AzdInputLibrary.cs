// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.TypeSpec.Generator.Input;
using TypeSpec.Azd.Generator.Input.Serialization;

namespace TypeSpec.Azd.Generator.Input;

public sealed class AzdInputLibrary : InputLibrary
{
    private readonly string _outputPath;
    private AzdInputNamespace? _inputNamespace;

    public AzdInputLibrary(string outputPath) : base(outputPath)
    {
        _outputPath = outputPath;
    }

    public override AzdInputNamespace InputNamespace => _inputNamespace ??= LoadAzdNamespace();

    private AzdInputNamespace LoadAzdNamespace()
    {
        var path = Path.Combine(_outputPath, "tspCodeModel.json");
        var json = File.ReadAllText(path);
        var input = TypeSpecSerialization.Deserialize(json)
            ?? throw new JsonException("The MTG code model cannot be null.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            MaxDepth = 128,
        });
        if (!document.RootElement.TryGetProperty("extension", out var extension))
        {
            throw new JsonException("The code model must contain an 'extension' section.");
        }
        return new AzdInputNamespace(input, AzdInputSerialization.Deserialize(extension, input))
        {
            InvalidNamespaceSegments = input.InvalidNamespaceSegments,
        };
    }
}
