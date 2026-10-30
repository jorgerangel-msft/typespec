// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.TypeSpec.Generator.Input;
using NUnit.Framework;
using TypeSpec.Azd.Generator.Input;

namespace TypeSpec.Azd.Generator.Tests;

public class AzdInputLibraryTests
{
    private string _directory = string.Empty;
    private string _fixture = string.Empty;
    private string CodeModelPath => Path.Combine(_directory, "tspCodeModel.json");

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"input-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _fixture = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "tspCodeModel.json"));
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void LoadsSharedAndExtensionInputsThroughTheVirtualProperty()
    {
        File.WriteAllText(CodeModelPath, _fixture);
        InputLibrary library = new AzdInputLibrary(_directory);
        var input = library.InputNamespace;
        Assert.That(input, Is.TypeOf<AzdInputNamespace>());
        var azd = (AzdInputNamespace)input;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(azd.Name, Is.EqualTo("Contoso.Generated"));
            Assert.That(azd.ApiVersions, Is.EqualTo(new[] { "2026-10-30" }));
            Assert.That(azd.Constants, Has.Count.EqualTo(2));
            Assert.That(azd.Extension.Id, Is.EqualTo("contoso.azd.tagger"));
            Assert.That(azd.Extension.Commands[0].Result, Is.SameAs(azd.Constants[0]));
            Assert.That(azd.Extension.Commands[1].Result, Is.SameAs(azd.Constants[0]));
            Assert.That(azd.Constants[1].ValueType, Is.SameAs(azd.Constants[0].ValueType));
            Assert.That(azd.Extension.Commands[0].Result.Value, Is.EqualTo("Hello from the input library!"));
        }
    }

    [Test]
    public void LoadsLazilyAndCachesTheCombinedNamespace()
    {
        var library = new AzdInputLibrary(_directory);
        File.WriteAllText(CodeModelPath, _fixture);
        var input = library.InputNamespace;
        File.WriteAllText(CodeModelPath, "invalid JSON");
        Assert.That(library.InputNamespace, Is.SameAs(input));
    }

    [Test]
    public void ReportsAMissingCodeModel()
    {
        var library = new AzdInputLibrary(_directory);
        Assert.Throws<FileNotFoundException>(() => _ = library.InputNamespace);
    }

    [TestCase("missing-root-name", "Required property 'name'")]
    [TestCase("missing-extension", "must contain an 'extension' section")]
    [TestCase("null-extension", "extension section cannot be null")]
    [TestCase("unsupported-version", "Unsupported extension IR version 2")]
    [TestCase("unknown-result", "references unknown result constant")]
    [TestCase("wrong-result-type", "must reference a string literal result")]
    [TestCase("duplicate-command", "Duplicate extension command")]
    [TestCase("duplicate-constant", "Duplicate code-model constant")]
    [TestCase("no-commands", "must contain at least one command")]
    [TestCase("missing-result", "missing required")]
    public void ReportsMalformedInput(string scenario, string message)
    {
        var model = JsonNode.Parse(_fixture) ?? throw new InvalidOperationException("Missing test fixture.");
        switch (scenario)
        {
            case "missing-root-name":
                model.AsObject().Remove("name");
                break;
            case "missing-extension":
                model.AsObject().Remove("extension");
                break;
            case "null-extension":
                model["extension"] = null;
                break;
            case "unsupported-version":
                model["extension"]!["schemaVersion"] = 2;
                break;
            case "unknown-result":
                model["extension"]!["commands"]![0]!["result"] = "missing";
                break;
            case "wrong-result-type":
                model["constants"]![0]!["valueType"]!["kind"] = "boolean";
                model["constants"]![0]!["value"] = true;
                model["constants"]![1]!["value"] = false;
                break;
            case "duplicate-command":
                model["extension"]!["commands"]![1]!["name"] = "show";
                break;
            case "duplicate-constant":
                model["constants"]![1]!["name"] = "show.result";
                break;
            case "no-commands":
                model["extension"]!["commands"] = new JsonArray();
                break;
            case "missing-result":
                model["extension"]!["commands"]![0]!.AsObject().Remove("result");
                break;
        }
        File.WriteAllText(CodeModelPath, model.ToJsonString());
        var library = new AzdInputLibrary(_directory);
        var exception = Assert.Throws<JsonException>(() => _ = library.InputNamespace);
        Assert.That(exception!.Message, Does.Contain(message));
    }
}
