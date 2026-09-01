// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ClientModel.Primitives;
using System.Linq;
using System.Text.Json;
using SampleTypeSpec;
using NUnit.Framework;

namespace Microsoft.TypeSpec.Generator.ClientModel.Tests.ModelReaderWriterValidation.TestProjects.Sample_TypeSpec
{
    /// <summary>
    /// Validates the generated shape proposed for the `composes` client option
    /// (https://github.com/microsoft/typespec/issues/11715).
    ///
    /// The extension properties under test are hand-written stand-ins for what the emitter would
    /// generate. They live in SampleTypeSpec/src/Custom/My{Bird,Lizard,Shark}Extensions.cs and are
    /// backed entirely by the target's JsonPatch.
    ///
    /// Three targets cover the three usage patterns that drive whether composed properties get a
    /// setter:
    ///
    ///   MyBird    Input          -> get + set
    ///   MyLizard  Output         -> get only
    ///   MyShark   Input, Output  -> get + set
    /// </summary>
    internal class ComposedModelsTests
    {
#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

        #region MyBird -- Input usage -> get + set

        [Test]
        public void Bird_ComposedProperties_AreWrittenToTheWire()
        {
            var bird = new MyBird("blue", 2)
            {
                Nickname = "tweety",
                Wingspan = 12.5f,
            };

            var json = ModelReaderWriter.Write(bird, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // The target's own declared properties are unaffected and written exactly once.
            Assert.That(GetRootPropertyCount(root, "color"), Is.EqualTo(1));
            Assert.That(root.GetProperty("color").GetString(), Is.EqualTo("blue"));
            Assert.That(root.GetProperty("age").GetInt32(), Is.EqualTo(2));

            // Composed properties are flushed by the trailing Patch.WriteTo(writer), with no
            // serializer changes required on either side.
            Assert.That(root.GetProperty("nickname").GetString(), Is.EqualTo("tweety"));
            Assert.That(root.GetProperty("wingspan").GetSingle(), Is.EqualTo(12.5f));
        }

        [Test]
        public void Bird_ComposedProperties_RoundTripThroughTheWire()
        {
            var bird = new MyBird("blue", 2)
            {
                Nickname = "tweety",
                Wingspan = 12.5f,
            };

            var data = ModelReaderWriter.Write(bird, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);
            var roundTripped = ModelReaderWriter.Read<MyBird>(data, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            Assert.That(roundTripped.Color, Is.EqualTo("blue"));
            Assert.That(roundTripped.Nickname, Is.EqualTo("tweety"));
            Assert.That(roundTripped.Wingspan, Is.EqualTo(12.5f));

            // Writing the round-tripped instance again is stable and does not duplicate keys.
            var reserialized = ModelReaderWriter.Write(roundTripped, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(reserialized);
            Assert.That(GetRootPropertyCount(document.RootElement, "color"), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(document.RootElement, "nickname"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("nickname").GetString(), Is.EqualTo("tweety"));
        }

        [Test]
        public void Bird_ComposedProperties_ReadValuesDeserializedByTheTargetsOwnReader()
        {
            // The instance is produced by the target's own reader, which knows nothing about the
            // composing model. Unknown wire data lands in the patch, so composed properties resolve.
            var bird = ModelReaderWriter.Read<MyBird>(
                BinaryData.FromString("""{"color":"red","age":5,"nickname":"from-wire","wingspan":9.25}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            Assert.That(bird.Color, Is.EqualTo("red"));
            Assert.That(bird.Nickname, Is.EqualTo("from-wire"));
            Assert.That(bird.Wingspan, Is.EqualTo(9.25f));
        }

        [Test]
        public void Bird_ComposedProperties_UnsetValuesReturnNullAndAreOmitted()
        {
            var bird = new MyBird("blue", 2);

            Assert.That(bird.Nickname, Is.Null);
            Assert.That(bird.Wingspan, Is.Null);

            var json = ModelReaderWriter.Write(bird, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.TryGetProperty("nickname", out _), Is.False);
            Assert.That(document.RootElement.TryGetProperty("wingspan", out _), Is.False);
        }

        [Test]
        public void Bird_ComposedProperties_SettingNullRemovesTheKey()
        {
            var bird = new MyBird("blue", 2)
            {
                Nickname = "tweety",
                Wingspan = 12.5f,
            };

            bird.Nickname = null;
            bird.Wingspan = null;

            // Regression guard: after Remove, JsonPatch.TryGetValue still reports true with an
            // empty value, so the getters must check IsRemoved first or Nickname returns "".
            Assert.That(bird.Nickname, Is.Null);
            Assert.That(bird.Wingspan, Is.Null);

            var json = ModelReaderWriter.Write(bird, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.TryGetProperty("nickname", out _), Is.False);
            Assert.That(document.RootElement.TryGetProperty("wingspan", out _), Is.False);
            Assert.That(document.RootElement.GetProperty("color").GetString(), Is.EqualTo("blue"));
        }

        [Test]
        public void Bird_ComposedProperties_CanBeUpdatedAfterBeingSet()
        {
            var bird = new MyBird("blue", 2) { Nickname = "first" };
            bird.Nickname = "second";

            Assert.That(bird.Nickname, Is.EqualTo("second"));

            var json = ModelReaderWriter.Write(bird, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(json);
            Assert.That(GetRootPropertyCount(document.RootElement, "nickname"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("nickname").GetString(), Is.EqualTo("second"));
        }

        [Test]
        public void Bird_ComposedProperties_AreSettableEvenThoughTargetPropertiesAreNot()
        {
            // MyBird is input-only, so its own required properties are constructor-assigned and
            // get-only. Composed properties have no constructor hook, so they are settable anyway.
            // This is the deliberate divergence from PropertyProvider.PropertyHasSetter.
            var bird = new MyBird("blue", 2);
            bird.Nickname = "settable";

            Assert.That(bird.Nickname, Is.EqualTo("settable"));
            Assert.That(bird.Color, Is.EqualTo("blue"));
        }

        #endregion

        #region MyLizard -- Output usage -> get only

        [Test]
        public void Lizard_ComposedProperties_ReadValuesFromTheWire()
        {
            var lizard = ReadLizard();

            Assert.That(lizard.Species, Is.EqualTo("gecko"));
            Assert.That(lizard.LengthInches, Is.EqualTo(8));

            Assert.That(lizard.Habitat, Is.EqualTo("desert"));
            Assert.That(lizard.Tracker, Is.Not.Null);
            Assert.That(lizard.Tracker.Bar, Is.EqualTo("tracker-1"));
        }

        [Test]
        public void Lizard_ComposedProperties_SurviveReserialization()
        {
            var lizard = ReadLizard();

            var json = ModelReaderWriter.Write(lizard, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.That(GetRootPropertyCount(root, "species"), Is.EqualTo(1));
            Assert.That(root.GetProperty("habitat").GetString(), Is.EqualTo("desert"));
            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("tracker-1"));
        }

        [Test]
        public void Lizard_ComposedProperties_AbsentValuesReturnDefaults()
        {
            // Produced by the model factory, so nothing is in the patch at all.
            var lizard = SampleTypeSpecModelFactory.MyLizard(species: "iguana", lengthInches: 20);

            Assert.That(lizard.Habitat, Is.Null);
            Assert.That(lizard.Tracker, Is.Null);

            var json = ModelReaderWriter.Write(lizard, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.TryGetProperty("habitat", out _), Is.False);
            Assert.That(document.RootElement.TryGetProperty("tracker", out _), Is.False);
        }

        [Test]
        public void Lizard_ComposedModelProperty_DoesNotThrowWhenRemoved()
        {
            var lizard = ReadLizard();
            Assert.That(lizard.Tracker, Is.Not.Null);

            // A removed key still reports TryGetJson == true, but hands back a zero-length buffer
            // that ModelReaderWriter.Read cannot parse. The getter must guard for it.
            lizard.Patch.Remove("$.tracker"u8);

            Assert.That(lizard.Tracker, Is.Null);
        }

        #endregion

        #region MyShark -- Input + Output usage -> get + set

        [Test]
        public void Shark_ComposedProperties_RoundTripThroughTheWire()
        {
            var shark = new MyShark("hammerhead", 5)
            {
                Tag = "tag-1",
                Tracker = new TrackerReference("tracker-1"),
            };

            var data = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);
            var roundTripped = ModelReaderWriter.Read<MyShark>(data, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            Assert.That(roundTripped.Name, Is.EqualTo("hammerhead"));
            Assert.That(roundTripped.FinCount, Is.EqualTo(5));
            Assert.That(roundTripped.Tag, Is.EqualTo("tag-1"));
            Assert.That(roundTripped.Tracker, Is.Not.Null);
            Assert.That(roundTripped.Tracker.Bar, Is.EqualTo("tracker-1"));
        }

        [Test]
        public void Shark_ComposedProperties_CanBeModifiedOnAModelReturnedFromTheService()
        {
            // The round-trip scenario composed properties exist for: read a model off the wire,
            // modify it, send it back.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tag":"original"}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            Assert.That(shark.Tag, Is.EqualTo("original"));

            shark.Tag = "updated";
            shark.Name = "updated-mako";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(json);
            Assert.That(GetRootPropertyCount(document.RootElement, "tag"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tag").GetString(), Is.EqualTo("updated"));
            Assert.That(document.RootElement.GetProperty("name").GetString(), Is.EqualTo("updated-mako"));
        }

        [Test]
        public void Shark_ComposedModelProperty_IsSettableUnlikeLizards()
        {
            // The same TrackerReference property is get-only on MyLizard (Output) and settable
            // here (Input + Output). Target usage is the only thing that differs.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("first"),
            };

            shark.Tracker = new TrackerReference("second");

            Assert.That(shark.Tracker!.Bar, Is.EqualTo("second"));

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(json);
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("second"));
        }

        [Test]
        public void Shark_ComposedProperties_SettingNullRemovesTheKey()
        {
            var shark = new MyShark("hammerhead", 5)
            {
                Tag = "tag-1",
                Tracker = new TrackerReference("tracker-1"),
            };

            shark.Tag = null;
            shark.Tracker = null;

            Assert.That(shark.Tag, Is.Null);
            Assert.That(shark.Tracker, Is.Null);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.TryGetProperty("tag", out _), Is.False);
            Assert.That(document.RootElement.TryGetProperty("tracker", out _), Is.False);
            Assert.That(document.RootElement.GetProperty("name").GetString(), Is.EqualTo("hammerhead"));
        }

        [Test]
        public void Shark_ComposedModelProperty_DoesNotPreserveReferenceIdentity()
        {
            // Documents a known limitation of the patch-backed design: the getter deserializes a
            // fresh instance on every access, so mutating the result does not write back.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("tracker-1"),
            };

            Assert.That(ReferenceEquals(shark.Tracker, shark.Tracker), Is.False);

            shark.Tracker!.Bar = "mutated";

            Assert.That(shark.Tracker!.Bar, Is.EqualTo("tracker-1"));
        }

        #endregion

#pragma warning restore SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

        private static MyLizard ReadLizard()
        {
            return ModelReaderWriter.Read<MyLizard>(
                BinaryData.FromString(
                    """
                    {
                      "species": "gecko",
                      "lengthInches": 8,
                      "habitat": "desert",
                      "tracker": { "bar": "tracker-1" }
                    }
                    """),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;
        }

        private static int GetRootPropertyCount(JsonElement root, string propertyName) => root.EnumerateObject().Count(property => property.NameEquals(propertyName));
    }
}
