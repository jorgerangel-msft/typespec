// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    /// backed by scalar patch values or retained ordinary model references.
    /// ComposesCallbackRequirement tests remain enabled even where the strict write-only baseline
    /// cannot fulfill them. Characterizations and opt-in overlay comparisons are not substitutes
    /// for those requirements.
    ///
    /// Three targets cover the three usage patterns that drive whether composed properties get a
    /// setter:
    ///
    ///   MyBird    Input          -> get + set
    ///   MyLizard  Output         -> get only
    ///   MyShark   Input, Output  -> get + set
    /// </summary>
    internal partial class ComposedModelsTests
    {
#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

        [Test]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_RegistrationMayHappenAfterDeserialization()
        {
            Assert.That(s_lateLibraryInitialized, Is.False);
            var received = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"late":{"bar":"service"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            using var beforeRegistration = JsonDocument.Parse(ModelReaderWriter.Write(
                received, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(beforeRegistration.RootElement.GetProperty("late").GetProperty("bar").GetString(), Is.EqualTo("service"));
            Assert.That(s_lateLibraryInitialized, Is.False, "Reading/writing a target must not initialize a composing library.");

            var constructed = new MyShark("mako", 4);
            var child = new TrackerReference("live");
            LateComposingLibrary.Set(constructed, child);
            Assert.That(s_lateLibraryInitialized, Is.True);
            child.Bar = "mutated";
            using var afterRegistration = JsonDocument.Parse(ModelReaderWriter.Write(
                constructed, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(afterRegistration.RootElement.GetProperty("late").GetProperty("bar").GetString(), Is.EqualTo("mutated"));
        }

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
        public void Shark_ComposedModelProperty_PreservesReferenceIdentity()
        {
            // The composed child is held in a ConditionalWeakTable, so repeated reads hand back
            // the same instance rather than deserializing a fresh one each time.
            var tracker = new TrackerReference("tracker-1");
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = tracker,
            };

            Assert.That(shark.Tracker, Is.SameAs(tracker));
            Assert.That(shark.Tracker, Is.SameAs(shark.Tracker));
        }

        [Test]
        public void Shark_ComposedModelProperty_MutationThroughTheGetterReachesTheWire()
        {
            // THE CORE ISSUE. `shark.Tracker.Bar = ...` compiles to a getter call followed by a
            // mutation, so the extension setter never runs.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("tracker-1"),
            };

            shark.Tracker!.Bar = "mutated";

            Assert.That(shark.Tracker!.Bar, Is.EqualTo("mutated"));

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("mutated"));

            // The target's own properties are untouched and not duplicated.
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
            Assert.That(root.GetProperty("name").GetString(), Is.EqualTo("hammerhead"));
            Assert.That(root.GetProperty("finCount").GetInt32(), Is.EqualTo(5));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ComposedModelProperty_MutationOnAServiceReturnedModelReachesTheWire()
        {
            // Deserialize -> mutate the composed child -> send back, without ever assigning the
            // composed property. This is the path the feature exists for.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(
                    """
                    {
                      "name": "hammerhead",
                      "finCount": 5,
                      "tracker": { "bar": "from-service" }
                    }
                    """),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            Assert.That(shark.Tracker!.Bar, Is.EqualTo("from-service"));

            shark.Tracker!.Bar = "mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("mutated"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ComposedModelProperty_UnknownChildPropertiesSurviveMutation()
        {
            // Only the composed child is re-serialized, by its own serializer, so properties the
            // library does not model are preserved.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(
                    """
                    {
                      "name": "hammerhead",
                      "finCount": 5,
                      "tracker": { "bar": "from-service", "unknown": "kept" }
                    }
                    """),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            shark.Tracker!.Bar = "mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("mutated"));
            Assert.That(tracker.GetProperty("unknown").GetString(), Is.EqualTo("kept"));
        }

        [Test]
        public void Shark_ComposedModelProperty_NeverTouchedPropertyRoundTripsThroughThePatch()
        {
            // If the extension is never used, the patch alone must round-trip the property.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(
                    """
                    {
                      "name": "hammerhead",
                      "finCount": 5,
                      "tracker": { "bar": "from-service", "unknown": "kept" }
                    }
                    """),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("from-service"));
            Assert.That(tracker.GetProperty("unknown").GetString(), Is.EqualTo("kept"));
        }

        [Test]
        public void Shark_ComposedModelProperty_OwnershipTransferIsOrderIndependent()
        {
            // Materializing unchanged data must preserve the payload. Under the strict guard,
            // this does not transfer serialization ownership away from the received patch.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"hammerhead","finCount":5,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            var beforeRead = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            _ = shark.Tracker;
            var afterRead = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(afterRead, Is.EqualTo(beforeRead));
        }

        [Test]
        public void Shark_ComposedModelProperty_JsonNullRoundTrips()
        {
            // An explicit JSON null carries no object, so the composed property reads as null. The
            // null itself stays in the patch, so it still reaches the wire untouched.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"hammerhead","finCount":5,"tracker":null}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            Assert.That(shark.Tracker, Is.Null);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ComposedModelProperty_ServiceNullThenAssignmentWritesTheAssignedValue()
        {
            // Desired CLR assignment semantics; the strict guard still gives received null to Patch.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"hammerhead","finCount":5,"tracker":null}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            shark.Tracker = new TrackerReference("assigned");

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("assigned"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ComposedModelProperty_DeserializeThenMutateReachesTheWire()
        {
            // The scenario the whole design exists for: the value arrives from the service, the
            // caller mutates the live object through the extension property, and the mutation --
            // not the stale received value -- is what reaches the wire.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            shark.Tracker!.Bar = "mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("mutated"));
        }

        [Test]
        public void Shark_ComposedModelProperty_ReassignmentReplacesTheChild()
        {
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("first"),
            };

            var replacement = new TrackerReference("second");
            shark.Tracker = replacement;

            Assert.That(shark.Tracker, Is.SameAs(replacement));

            shark.Tracker!.Bar = "second-mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("second-mutated"));
        }

        [Test]
        public void Shark_ComposedModelProperty_StaleReferenceDoesNotAffectTheReplacement()
        {
            var shark = new MyShark("hammerhead", 5);
            var first = new TrackerReference("first");
            shark.Tracker = first;
            shark.Tracker = new TrackerReference("second");

            first.Bar = "stale-write";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("second"));
        }

        [Test]
        public void Shark_ComposedModelProperty_RepeatedAccessIsStable()
        {
            // The write callback is subscribed once per library, from the static constructor, so
            // reading the property repeatedly must not stack subscriptions.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("tracker-1"),
            };

            for (int i = 0; i < 1000; i++)
            {
                _ = shark.Tracker;
            }

            shark.Tracker!.Bar = "mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("mutated"));
        }

        [Test]
        public void Shark_ComposedWriters_RunOncePerSerializationAndSupportMultipleLibraries()
        {
            // Registers a second writer the way an independent composing library would, which
            // exercises the public callback contract and proves two libraries coexist.
            EnsureCountingWriterRegistered();

            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("tracker-1"),
            };

            for (int i = 0; i < 100; i++)
            {
                _ = shark.Tracker;
            }

            s_countingWriterInvocations = 0;
            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(
                s_countingWriterInvocations,
                Is.EqualTo(1),
                "the callback runs once per serialization, not per property or per access");

            // The other library's property is unaffected by ours.
            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("tracker-1"));
        }

        private static int s_countingWriterInvocations;
        private static bool s_countingWriterRegistered;

        private static void EnsureCountingWriterRegistered()
        {
            if (s_countingWriterRegistered)
            {
                return;
            }
            s_countingWriterRegistered = true;
            MyShark.AddComposedWriter(static (_, _, _) => s_countingWriterInvocations++);
        }

        [Test]
        public void Shark_TwoComposedPropertiesOfTheSameType_AreIndependent()
        {
            // Keyed by JSON path, not by model type, so same-typed properties cannot collide.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("primary"),
                BackupTracker = new TrackerReference("backup"),
            };

            Assert.That(shark.Tracker, Is.Not.SameAs(shark.BackupTracker));

            shark.Tracker!.Bar = "primary-mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("primary-mutated"));
            Assert.That(root.GetProperty("backupTracker").GetProperty("bar").GetString(), Is.EqualTo("backup"));
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(root, "backupTracker"), Is.EqualTo(1));
        }

        [Test]
        [Category("ComposesOverlayComparison")]
        public void Shark_ComposedModelProperty_RawPatchWriteAtTheAnchorIsMergedUnderTheLiveModel()
        {
            // Comparison only: this live-wins policy violates the requested raw-anchor precedence.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("live"),
            };
            UseLiveOverlayForComparison(shark);

            shark.Patch.Set("$.tracker"u8, """{"bar":"raw","rawOnly":"kept"}"""u8);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("live"));
            Assert.That(tracker.GetProperty("rawOnly").GetString(), Is.EqualTo("kept"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        [Category("ComposesPerLeafAlternative")]
        public void Shark_ComposedModelProperty_RawWriteBelowTheAnchorIsMergedNotLost()
        {
            // THE CASE THIS DESIGN EXISTS FOR. A service field the SDK does not model yet: the
            // caller reaches for raw patch access, which is the reason @dynamicModel exists.
            // The composed property must not swallow it.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("known"),
            };

            shark.Patch.Set("$.tracker.newServiceField"u8, "\"value\""u8);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("known"));
            Assert.That(tracker.GetProperty("newServiceField").GetString(), Is.EqualTo("value"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        [Category("ComposesPerLeafAlternative")]
        public void Shark_ComposedModelProperty_RawWriteBelowTheAnchorSurvivesALaterClrMutation()
        {
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("known"),
            };

            shark.Patch.Set("$.tracker.newServiceField"u8, "\"value\""u8);
            shark.Tracker!.Bar = "mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("mutated"), "an unedited modeled sibling must survive");
            Assert.That(tracker.GetProperty("newServiceField").GetString(), Is.EqualTo("value"), "a raw write the model does not know about is preserved");
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        [Category("ComposesPerLeafAlternative")]
        public void Shark_ComposedModelProperty_RawWriteOrderDoesNotMatter()
        {
            // Raw first, then compose -- same outcome as compose-then-raw.
            var shark = new MyShark("hammerhead", 5);
            shark.Patch.Set("$.tracker.newServiceField"u8, "\"value\""u8);
            shark.Tracker = new TrackerReference("known");

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));
            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("known"));
            Assert.That(tracker.GetProperty("newServiceField").GetString(), Is.EqualTo("value"));
        }

        [Test]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_ComposedModelProperty_DeepRawWriteStaysUnderItsReceivedAnchor()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(
                    """{"name":"mako","finCount":4,"tracker":{"bar":"svc","nested":{"known":"a"}}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            _ = shark.Tracker;
            shark.Patch.Set("$.tracker.nested.deep"u8, "\"raw\""u8);
            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.True);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);

            var nested = document.RootElement.GetProperty("tracker").GetProperty("nested");
            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));
            Assert.That(nested.GetProperty("known").GetString(), Is.EqualTo("a"));

            Assert.That(document.RootElement.TryGetProperty("deep", out _), Is.False);
            Assert.That(nested.GetProperty("deep").GetString(), Is.EqualTo("raw"));
        }

        [Test]
        public void Shark_ComposedModelProperty_SerializingTwiceIsStable()
        {
            // The default callback is read-only; repeated writes must remain stable.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("known"),
            };
            shark.Patch.Set("$.tracker.newServiceField"u8, "\"value\""u8);

            var first = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            var second = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            var third = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(second, Is.EqualTo(first));
            Assert.That(third, Is.EqualTo(second));
        }

        [Test]
        public void Shark_ComposedModelProperty_DoesNotPinTheTarget()
        {
            // The write callback is static and the state lives in a ConditionalWeakTable keyed by
            // the target, so composing must not keep the target alive.
            var probe = Compose();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.That(probe.IsAlive, Is.False);

            static WeakReference Compose()
            {
                var shark = new MyShark("transient", 1)
                {
                    Tracker = new TrackerReference("tracker-1"),
                };
                shark.Tracker!.Bar = "mutated";
                return new WeakReference(shark);
            }
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ScalarAndModelComposedProperties_CoexistOnTheSameInstance()
        {
            // Scalar changes can update Patch directly; an ordinary child's mutation cannot.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(
                    """
                    {
                      "name": "mako",
                      "finCount": 4,
                      "tag": "from-service",
                      "tracker": { "bar": "from-service" }
                    }
                    """),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            // Reading the model materializes it without claiming the parent's patch entry.
            Assert.That(shark.Tag, Is.EqualTo("from-service"));
            Assert.That(shark.Tracker!.Bar, Is.EqualTo("from-service"));

            shark.Tag = "updated";
            shark.Tracker!.Bar = "mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.That(GetRootPropertyCount(root, "tag"), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
            Assert.That(root.GetProperty("tag").GetString(), Is.EqualTo("updated"));
            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("mutated"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ModelComposedProperty_AssignedAfterDeserializationDoesNotDuplicateTheKey()
        {
            // One key is necessary but insufficient: it must also contain the assigned value.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            // Assign without materializing the received child first.
            shark.Tracker = new TrackerReference("assigned");

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1),
                "a duplicate key is invisible to JsonDocument, so count the raw text");

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("assigned"));
        }

        [Test]
        [Category("ComposesCallbackBaseline")]
        public void Shark_ReadingAComposedModelLeavesReceivedPatchUnchanged()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(
                    """{"name":"mako","finCount":4,"tag":"from-service","tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;
            string before = shark.Patch.ToBinaryData().ToString();

            Assert.That(shark.Tracker, Is.Not.Null);
            Assert.That(shark.Tracker!.Bar, Is.EqualTo("from-service"));

            Assert.That(shark.Tag, Is.EqualTo("from-service"));
            Assert.That(shark.Patch.Contains("$.tag"u8), Is.False);

            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.False, "all received names are source, not overrides");
            Assert.That(shark.Patch.IsRemoved("$.tracker"u8), Is.False);
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(before));
        }

        #endregion

        #region Received values -- private source and retained live models

        // Use the actual target reader, not explicit raw Set calls that would instead request
        // authoritative whole-member overrides.

        [Test]
        public void Shark_ReceivedValue_IsVisibleThroughTheGetter()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            Assert.That(shark.Tracker, Is.Not.Null, "the received value is materialized on first read");
            Assert.That(shark.Tracker!.Bar, Is.EqualTo("from-service"));
        }

        [Test]
        public void Shark_ReceivedValue_IsWrittenExactlyOnce()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            _ = shark.Tracker;

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("from-service"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ReceivedValue_MutationThroughTheGetterReachesTheWire()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            shark.Tracker!.Bar = "user-assigned";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("user-assigned"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ReceivedValue_AssignmentWithoutAPriorReadReachesTheWire()
        {
            // Desired assignment semantics with a received value that has not been materialized.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            shark.Tracker = new TrackerReference("user-assigned");

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("user-assigned"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ReceivedValue_PreservesRawFieldsTheModelDoesNotKnow()
        {
            // Mutation and preservation must hold together, not just an unchanged raw round-trip.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service","unmodelled":"kept"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            shark.Tracker!.Bar = "user-assigned";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));
            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("user-assigned"));
            Assert.That(tracker.GetProperty("unmodelled").GetString(), Is.EqualTo("kept"));
        }

        [Test]
        public void Shark_ConstructedValue_TakesTheFastPathAndNeverTouchesThePatch()
        {
            // A constructed child with no overlapping raw edit needs no serialization snapshot.
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("live"),
            };

            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.False);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.False, "the fast path folds nothing into the patch");
            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("live"));
        }

        [Test]
        public void Shark_TheWriterIsRegisteredBeforeItCanEverBeNeeded()
        {
            // Why a static constructor is enough, with no ordering guarantee required: the writer
            // only matters once this library holds a value for a composed property, and the only
            // ways to put one there are the extension accessors. Touching either runs the static
            // constructor first, so registration cannot be late.
            //
            // Deserializing touches MyShark alone. If registration were required at that moment,
            // this would be the case that broke -- instead the value simply waits in the patch.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"from-service"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            var beforeAnyAccess = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(beforeAnyAccess, "tracker"), Is.EqualTo(1), "untouched source round-trips");

            using (var document = JsonDocument.Parse(beforeAnyAccess))
            {
                Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("from-service"));
            }

            // First access through the extension: the value is still there to be found.
            Assert.That(shark.Tracker!.Bar, Is.EqualTo("from-service"));
        }

        [Test]
        [Category("ComposesCallbackBaseline")]
        public void Shark_ComposedProperty_IsCapturedAsSourceWithoutReadRegistration()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"svc"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.False);
            Assert.That(shark.Tracker!.Bar, Is.EqualTo("svc"));
        }

        [Test]
        public void Shark_UndeclaredUnknownProperty_StillRoundTrips()
        {
            // A property no composing library knows must still be carried by the target.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"somethingNobodyModels":"kept"}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            Assert.That(shark.Patch.Contains("$.somethingNobodyModels"u8), Is.False);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("somethingNobodyModels").GetString(), Is.EqualTo("kept"));
        }

        [Test]
        public void Shark_UnaccessedComposedProperty_SurvivesAPureRoundTrip()
        {
            // Untouched received JSON does not require typed materialization.
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"svc","unknown":"kept"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("svc"));
            Assert.That(tracker.GetProperty("unknown").GetString(), Is.EqualTo("kept"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ReceivedComposedMutationMustSurviveWithoutReadRegistration()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"svc"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            shark.Tracker!.Bar = "mutated";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.False, "received unknown JSON is source, not an override");
            Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("mutated"));
        }

        #endregion

        #region AddComposedWriter -- the registration contract

        [Test]
        public void AddComposedWriter_ThrowsOnNull()
        {
            Assert.Throws<ArgumentNullException>(() => MyShark.AddComposedWriter(null!));
        }

        [Test]
        public void AddComposedWriter_ConcurrentRegistrationLosesNothing()
        {
            // Registration happens from a static constructor, so two composing libraries loading
            // on different threads can race. A plain `field += writer` is an unguarded
            // read-modify-write and drops registrations under contention; the interlocked
            // Delegate.Combine in the generated AddComposedWriter must not.
            const int threads = 16;

            int[] invoked = new int[threads];
            var writers = new Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions>[threads];
            for (int i = 0; i < threads; i++)
            {
                int index = i;
                writers[i] = (_, _, _) => Interlocked.Increment(ref invoked[index]);
            }

            Parallel.For(0, threads, i => MyShark.AddComposedWriter(writers[i]));

            var shark = new MyShark("hammerhead", 5);
            _ = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);

            // Every registration must have survived and run exactly once.
            Assert.That(invoked, Is.All.EqualTo(1));
        }

        [Test]
        public void AddComposedWriter_IsIdempotentForTheSameMethodGroup()
        {
            // A method group conversion creates a new delegate instance each time, but delegate
            // equality is target + method, so the second registration must be a no-op.
            s_methodGroupInvocations = 0;

            MyShark.AddComposedWriter(CountMethodGroupInvocation);
            MyShark.AddComposedWriter(CountMethodGroupInvocation);
            MyShark.AddComposedWriter(CountMethodGroupInvocation);

            var shark = new MyShark("hammerhead", 5);
            _ = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);

            Assert.That(s_methodGroupInvocations, Is.EqualTo(1),
                "a duplicate registration would write the composed property twice, producing a duplicate JSON key");
        }

        [Test]
        public void AddComposedWriter_IsIdempotentUnderConcurrentDuplicateRegistration()
        {
            // The dedup check lives inside the compare-exchange loop, so a lost race re-checks
            // against the winner rather than appending a duplicate.
            s_concurrentDuplicateInvocations = 0;

            Parallel.For(0, 32, _ => MyShark.AddComposedWriter(CountConcurrentDuplicateInvocation));

            var shark = new MyShark("hammerhead", 5);
            _ = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);

            Assert.That(s_concurrentDuplicateInvocations, Is.EqualTo(1));
        }

        [Test]
        public void AddComposedWriter_DoesNotDeduplicateDistinctCallbacks()
        {
            // Guard against over-deduplication: two different callbacks are two registrations,
            // even when their bodies are identical.
            s_distinctA = 0;
            s_distinctB = 0;

            MyShark.AddComposedWriter(CountDistinctA);
            MyShark.AddComposedWriter(CountDistinctB);

            var shark = new MyShark("hammerhead", 5);
            _ = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);

            Assert.That(s_distinctA, Is.EqualTo(1));
            Assert.That(s_distinctB, Is.EqualTo(1));
        }

        private static int s_methodGroupInvocations;
        private static int s_concurrentDuplicateInvocations;
        private static int s_distinctA;
        private static int s_distinctB;

        // Deliberately not lambdas: these are registered permanently, and writing nothing keeps
        // them from affecting any other test's payload.
        private static void CountMethodGroupInvocation(Utf8JsonWriter writer, MyShark shark, ModelReaderWriterOptions options)
            => Interlocked.Increment(ref s_methodGroupInvocations);

        private static void CountConcurrentDuplicateInvocation(Utf8JsonWriter writer, MyShark shark, ModelReaderWriterOptions options)
            => Interlocked.Increment(ref s_concurrentDuplicateInvocations);

        private static void CountDistinctA(Utf8JsonWriter writer, MyShark shark, ModelReaderWriterOptions options)
            => Interlocked.Increment(ref s_distinctA);

        private static void CountDistinctB(Utf8JsonWriter writer, MyShark shark, ModelReaderWriterOptions options)
            => Interlocked.Increment(ref s_distinctB);

        #endregion

        #region Composed child that owns its OWN JsonPatch

        [Test]
        public void Shark_ComposedChildWithItsOwnPatch_RoundTrips()
        {
            var shark = new MyShark("hammerhead", 5)
            {
                Beacon = new AnotherDynamicModel("ctor-only"),
            };

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            Assert.That(GetRootPropertyCount(document.RootElement, "beacon"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("beacon").GetProperty("bar").GetString(), Is.EqualTo("ctor-only"));

            var roundTripped = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(json), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            // A get-only, constructor-initialized property survives because the real instance is
            // retained and serialized by its own serializer -- no patch-backing required.
            Assert.That(roundTripped.Beacon!.Bar, Is.EqualTo("ctor-only"));
        }

        [Test]
        public void Shark_ComposedChildWithItsOwnPatch_ChildPatchMutationReachesTheWire()
        {
            // The child's own JsonPatch is authoritative for the child's subtree. The parent's
            // patch and the child's patch own disjoint scopes and must not interfere.
            var shark = new MyShark("hammerhead", 5)
            {
                Beacon = new AnotherDynamicModel("ctor-only"),
            };

            shark.Beacon!.Patch.Set("$.injected"u8, "\"via-child-patch\""u8);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var beacon = document.RootElement.GetProperty("beacon");

            Assert.That(beacon.GetProperty("bar").GetString(), Is.EqualTo("ctor-only"));
            Assert.That(beacon.GetProperty("injected").GetString(), Is.EqualTo("via-child-patch"));
        }

        [Test]
        public void Shark_ComposedChildWithItsOwnPatch_ChildPatchOverridesChildClrProperty()
        {
            // The child's ordinary dynamic-model rule (patch beats CLR) is unchanged by composing.
            var shark = new MyShark("hammerhead", 5)
            {
                Beacon = new AnotherDynamicModel("from-ctor"),
            };

            shark.Beacon!.Patch.Set("$.bar"u8, "\"from-patch\""u8);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var beacon = document.RootElement.GetProperty("beacon");

            Assert.That(GetRootPropertyCount(document.RootElement, "beacon"), Is.EqualTo(1));
            Assert.That(beacon.GetProperty("bar").GetString(), Is.EqualTo("from-patch"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        public void Shark_ComposedChildWithItsOwnPatch_SurvivesAServiceRoundTrip()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString(
                    """{"name":"mako","finCount":4,"beacon":{"bar":"from-service","unknown":"kept"}}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;

            Assert.That(shark.Beacon!.Bar, Is.EqualTo("from-service"));

            shark.Beacon!.Patch.Set("$.added"u8, "\"by-caller\""u8);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var beacon = document.RootElement.GetProperty("beacon");

            Assert.That(beacon.GetProperty("bar").GetString(), Is.EqualTo("from-service"));
            Assert.That(beacon.GetProperty("unknown").GetString(), Is.EqualTo("kept"));
            Assert.That(beacon.GetProperty("added").GetString(), Is.EqualTo("by-caller"));
        }

        #endregion

        #region Containment, ordering and format

        [Test]
        public void Shark_ComposedProperties_SurviveInsideACollection()
        {
            // The composed writer is invoked from the target's serializer, so it must fire
            // wherever that serializer runs -- including for each element of a collection.
            var sharks = new List<MyShark>
            {
                new("first", 1) { Tracker = new TrackerReference("t-1") },
                new("second", 2) { Tracker = new TrackerReference("t-2") },
            };

            sharks[0].Tracker!.Bar = "first-mutated";

            var json = ModelReaderWriter.Write(sharks, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var array = document.RootElement;

            Assert.That(array.GetArrayLength(), Is.EqualTo(2));
            Assert.That(array[0].GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("first-mutated"));
            Assert.That(array[1].GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("t-2"));
        }

        [Test]
        public void Shark_ComposedProperties_AreWrittenBeforeThePatch()
        {
            var shark = new MyShark("hammerhead", 5)
            {
                Tracker = new TrackerReference("t-1"),
            };
            shark.Patch.Set("$.zzzRaw"u8, "\"raw\""u8);

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

            Assert.That(names.IndexOf("name"), Is.LessThan(names.IndexOf("tracker")), "declared properties come first");
            Assert.That(names.IndexOf("tracker"), Is.LessThan(names.IndexOf("zzzRaw")), "the callback precedes Patch.WriteTo");
            Assert.That(names, Does.Contain("tracker"));
            Assert.That(names, Does.Contain("zzzRaw"));
        }

        [Test]
        [Category("ComposesCallbackBaseline")]
        public void Shark_ComposedProperties_PreserveJsonForBothParentFormats()
        {
            var child = ModelReaderWriter.Read<TrackerReference>(
                BinaryData.FromString("""{"bar":"from-service","unknown":"kept"}"""),
                ModelReaderWriterOptions.Json,
                SampleTypeSpecContext.Default)!;
            var shark = new MyShark("mako", 4) { Tracker = child };

            var jsonFormat = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
            var wireFormat = ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default).ToString();

            foreach (var json in new[] { jsonFormat, wireFormat })
            {
                Assert.That(CountRawOccurrences(json, "tracker"), Is.EqualTo(1));

                using var document = JsonDocument.Parse(json);
                Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("from-service"));
            }

            using var jsonDocument = JsonDocument.Parse(jsonFormat);
            using var wireDocument = JsonDocument.Parse(wireFormat);

            Assert.That(jsonDocument.RootElement.GetProperty("tracker").GetProperty("unknown").GetString(), Is.EqualTo("kept"));
            Assert.That(wireDocument.RootElement.GetProperty("tracker").GetProperty("unknown").GetString(), Is.EqualTo("kept"),
                "The chosen composed-value policy preserves JSON in either parent format.");

            using var standaloneWire = JsonDocument.Parse(ModelReaderWriter.Write(
                child, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default));
            Assert.That(standaloneWire.RootElement.TryGetProperty("unknown", out _), Is.False,
                "The ordinary child's own W serializer is unchanged.");
            Assert.That(JsonElement.DeepEquals(jsonDocument.RootElement.GetProperty("tracker"), wireDocument.RootElement.GetProperty("tracker")), Is.True);
        }

        [Test]
        public void Shark_ComposedModelProperty_RemoveThenReAddDoesNotResurrectTheOrphan()
        {
            var shark = new MyShark("hammerhead", 5);
            var orphan = new TrackerReference("orphan");
            shark.Tracker = orphan;
            shark.Tracker = null;
            shark.Tracker = new TrackerReference("re-added");

            orphan.Bar = "orphan-write";

            var json = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(json);
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("re-added"));
        }

        [Test]
        public void Shark_ComposedModelProperty_DetachedOrphanStaysSelfConsistent()
        {
            // A detached child is an ordinary object: it keeps its own state and stays usable,
            // unlike a "view" that would read through to whatever replaced it.
            var shark = new MyShark("hammerhead", 5);
            var orphan = new TrackerReference("orphan");
            shark.Tracker = orphan;
            shark.Tracker = null;

            Assert.DoesNotThrow(() => orphan.Bar = "still-usable");
            Assert.That(orphan.Bar, Is.EqualTo("still-usable"));
        }

        #endregion

        #region Write-only callback baseline and unresolved requirements

        [Test]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_RegistrationHasSerializationNamesButNoReadCallback()
        {
            var parameters = typeof(MyShark).GetMethod(nameof(MyShark.AddComposedWriter))!.GetParameters();

            Assert.That(parameters, Has.Length.EqualTo(2));
            Assert.That(parameters[0].ParameterType,
                Is.EqualTo(typeof(Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions>)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(string[])));
            Assert.That(parameters[1].IsDefined(typeof(ParamArrayAttribute)), Is.True);
            Assert.That(typeof(TrackerReference).GetProperty("Patch"), Is.Null);
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_ReceivedSourceDoesNotSuppressClrMutation(string format)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"service"}}"""),
                options, SampleTypeSpecContext.Default)!;

            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.False,
                "Unknown received JSON must not depend on callback registration.");
            var child = shark.Tracker;
            child.Bar = "changed";
            string operations = shark.Patch.ToBinaryData().ToString();

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));

            Assert.That(shark.Tracker, Is.SameAs(child));
            Assert.That(child.Bar, Is.EqualTo("changed"));
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("changed"),
                "Private source capture distinguishes received data from an explicit raw override.");
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(operations), "Writing must not mutate Patch.");
        }

        [TestCase("J", """{"bar":"raw","rawOnly":"kept"}""")]
        [TestCase("W", """{"bar":"raw","rawOnly":"kept"}""")]
        [TestCase("J", "null")]
        [TestCase("W", "null")]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_ExplicitRawAnchorWins(string format, string raw)
        {
            var shark = new MyShark("mako", 4) { Tracker = new TrackerReference("live") };
            shark.Patch.Set("$.tracker"u8, BinaryData.FromString(raw));
            string operations = shark.Patch.ToBinaryData().ToString();

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default));

            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tracker").GetRawText(), Is.EqualTo(raw));
            Assert.That(shark.Tracker.Bar, Is.EqualTo("live"));
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(operations));
        }

        [TestCase("J", "$.tracker.extra", """{"extra":"raw"}""")]
        [TestCase("W", "$.tracker.extra", """{"extra":"raw"}""")]
        [TestCase("J", "$.tracker.nested.extra", """{"nested":{"extra":"raw"}}""")]
        [TestCase("W", "$.tracker.nested.extra", """{"nested":{"extra":"raw"}}""")]
        [Category("ComposesCallbackCharacterization")]
        public void Shark_CallbackOnly_DescendantEditSuppressesTheLiveSubtree(string format, string path, string expected)
        {
            var shark = new MyShark("mako", 4) { Tracker = new TrackerReference("live") };
            shark.Patch.Set(Encoding.UTF8.GetBytes(path), "\"raw\""u8);
            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.True);
            string operations = shark.Patch.ToBinaryData().ToString();
            string? first = null;

            for (int i = 0; i < 3; i++)
            {
                string json = ModelReaderWriter.Write(
                    shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default).ToString();
                using var document = JsonDocument.Parse(json);
                Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
                Assert.That(document.RootElement.GetProperty("tracker").GetRawText(), Is.EqualTo(expected),
                    "Characterization only: raw descendants shadow even unedited live sibling fields.");
                first ??= json;
                Assert.That(json, Is.EqualTo(first));
                Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(operations));
            }
        }

        [Test]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_AssignedNullIsNotAnUninitializedGetter()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"service"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            var orphan = shark.Tracker;
            shark.Tracker = null;

            Assert.That(shark.Tracker, Is.Null, "An explicit null must not materialize the old received value again.");
            Assert.That(shark.Tracker, Is.Null);
            orphan.Bar = "still usable";
            Assert.That(orphan.Bar, Is.EqualTo("still usable"));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_RootOverrideBypassesComposedWriters(string format)
        {
            int calls = 0;
            MyShark.AddComposedWriter((_, target, _) =>
            {
                if (target.Name == "root-override-probe")
                {
                    calls++;
                }
            });
            var shark = new MyShark("root-override-probe", 4) { Tracker = new TrackerReference("live") };
            const string raw = """{"name":"raw","tracker":null}""";
            shark.Patch.Set("$"u8, BinaryData.FromString(raw));

            string json = ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default).ToString();

            Assert.That(json, Is.EqualTo(raw));
            Assert.That(calls, Is.Zero);
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_RawRemovalDoesNotChangeTheChildReference(string format)
        {
            var child = new TrackerReference("live");
            var shark = new MyShark("mako", 4) { Tracker = child };
            shark.Patch.Remove("$.tracker"u8);
            child.Bar = "still live";

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default));

            Assert.That(document.RootElement.TryGetProperty("tracker", out _), Is.False);
            Assert.That(shark.Tracker, Is.SameAs(child));
            Assert.That(shark.Tracker.Bar, Is.EqualTo("still live"));
        }

        [Test]
        [Category("ComposesCallbackBaseline")]
        public void AddComposedWriter_MulticastRegistrationIsAlsoIdempotent()
        {
            int[] calls = new int[2];
            Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> first = (_, target, _) =>
            {
                if (target.Name == "multicast-probe")
                {
                    calls[0]++;
                }
            };
            Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> second = (_, target, _) =>
            {
                if (target.Name == "multicast-probe")
                {
                    calls[1]++;
                }
            };

            MyShark.AddComposedWriter(first + second);
            MyShark.AddComposedWriter(first + second);
            MyShark.AddComposedWriter(first);
            _ = ModelReaderWriter.Write(
                new MyShark("multicast-probe", 4), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);

            Assert.That(calls, Is.All.EqualTo(1));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCallbackRequirement")]
        public void Shark_CallbackRequirement_ClearingAReceivedChildRemovesItFromTheWire(string format)
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"service"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            shark.Tracker = null;

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default));

            Assert.That(document.RootElement.TryGetProperty("tracker", out _), Is.False,
                "Unresolved requirement: clearing the retained reference must not resend the received child.");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCallbackRequirement")]
        public void Shark_CallbackRequirement_RawDeletedModeledPropertyIsNotReintroduced(string format)
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"service","unknown":"kept"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            _ = shark.Tracker;
            shark.Patch.Remove("$.tracker.bar"u8);

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default));
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(tracker.TryGetProperty("bar", out _), Is.False,
                "Raw removal must remain authoritative; a live-wins overlay reintroduces this property.");
            Assert.That(tracker.GetProperty("unknown").GetString(), Is.EqualTo("kept"));
        }

        [Test]
        [Category("ComposesCallbackCharacterization")]
        public void JsonPatch_NormalizedExportCannotDistinguishDescendantSetFromAnchorReplacement()
        {
            var descendant = new MyShark("mako", 4) { Tracker = new TrackerReference("live") };
            descendant.Patch.Set("$.tracker.extra"u8, "\"raw\""u8);
            var replacement = new MyShark("mako", 4) { Tracker = new TrackerReference("live") };
            replacement.Patch.Set("$.tracker"u8, """{"extra":"raw"}"""u8);

            Assert.That(descendant.Patch.Contains("$.tracker"u8), Is.True);
            Assert.That(replacement.Patch.Contains("$.tracker"u8), Is.True);
            Assert.That(descendant.Patch.ToBinaryData().ToString(), Is.EqualTo(replacement.Patch.ToBinaryData().ToString()));
            Assert.That(descendant.Patch.GetJson("$.tracker"u8).ToArray(),
                Is.EqualTo(replacement.Patch.GetJson("$.tracker"u8).ToArray()));

            using var operations = JsonDocument.Parse(descendant.Patch.ToBinaryData());
            Assert.That(operations.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(operations.RootElement[0].GetProperty("op").GetString(), Is.EqualTo("add"));
            Assert.That(operations.RootElement[0].GetProperty("path").GetString(), Is.EqualTo("/tracker"),
                "This is an effective anchor edit, not the original descendant operation.");
        }

        [Test]
        [Category("ComposesCallbackCharacterization")]
        public void JsonPatch_ScopedAnchorWriteIsNotAnObjectPropertySplicer()
        {
            var patch = new JsonPatch();
            patch.Set("$.tracker.extra"u8, "\"raw\""u8);
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            writer.WriteStartObject("tracker");
            writer.WriteString("bar", "live");

            Assert.Throws<InvalidOperationException>(() => patch.WriteTo(writer, "$.tracker"u8),
                "The stored object is a value; it cannot be written at an object property position.");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_OrdinaryGraphMutationsRetainIdentityAndUnknowns(string format)
        {
            var profile = ModelReaderWriter.Read<RoundTripModel>(
                BinaryData.FromString(ReadProfileJson()), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            var shark = new MyShark("mako", 4) { Profile = profile };
            var nested = profile.RequiredModel;

            shark.Profile.RequiredString = "live";
            shark.Profile.RequiredModel.OptionalNullableString = "nested";
            shark.Profile.RequiredModel.RequiredNullableList.Add(4);
            shark.Profile.RequiredRecordUnknown["clrOnly"] = BinaryData.FromString("\"live\"");

            Assert.That(typeof(RoundTripModel).GetProperty("Patch"), Is.Null);
            Assert.That(typeof(Thing).GetProperty("Patch"), Is.Null);
            Assert.That(shark.Profile, Is.SameAs(profile));
            Assert.That(shark.Profile.RequiredModel, Is.SameAs(nested));

            var options = new ModelReaderWriterOptions(format);
            string first = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            nested.RequiredNullableList.Add(5);
            string second = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();

            using var firstDocument = JsonDocument.Parse(first);
            using var secondDocument = JsonDocument.Parse(second);
            var written = secondDocument.RootElement.GetProperty("profile");
            Assert.That(written.GetProperty("requiredString").GetString(), Is.EqualTo("live"));
            Assert.That(written.GetProperty("requiredModel").GetProperty("optionalNullableString").GetString(), Is.EqualTo("nested"));
            Assert.That(written.GetProperty("requiredModel").GetProperty("requiredNullableList").EnumerateArray().Select(x => x.GetInt32()),
                Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
            Assert.That(firstDocument.RootElement.GetProperty("profile").GetProperty("requiredModel").GetProperty("requiredNullableList").GetArrayLength(),
                Is.EqualTo(4));
            Assert.That(written.GetProperty("requiredRecordUnknown").GetProperty("clrOnly").GetString(), Is.EqualTo("live"));
            Assert.That(written.GetProperty("extra").GetString(), Is.EqualTo("stuff"));
            Assert.That(shark.Patch.Contains("$.profile"u8), Is.False);
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCallbackBaseline")]
        public void Shark_CallbackOnly_SharingAndReassignmentKeepOrdinaryObjectSemantics(string format)
        {
            var shared = new TrackerReference("shared");
            var first = new MyShark("first", 1) { Tracker = shared, BackupTracker = shared };
            var second = new MyShark("second", 2) { Tracker = shared };
            first.Tracker = new TrackerReference("replacement");
            shared.Bar = "shared mutation";
            var options = new ModelReaderWriterOptions(format);

            Assert.That(first.Tracker, Is.Not.SameAs(shared));
            Assert.That(first.BackupTracker, Is.SameAs(shared));
            Assert.That(second.Tracker, Is.SameAs(shared));
            using var firstDocument = JsonDocument.Parse(ModelReaderWriter.Write(first, options, SampleTypeSpecContext.Default));
            using var secondDocument = JsonDocument.Parse(ModelReaderWriter.Write(second, options, SampleTypeSpecContext.Default));
            Assert.That(firstDocument.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("replacement"));
            Assert.That(firstDocument.RootElement.GetProperty("backupTracker").GetProperty("bar").GetString(), Is.EqualTo("shared mutation"));
            Assert.That(secondDocument.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("shared mutation"));
        }

        [Test]
        [Category("ComposesCallbackRequirement")]
        [Category("ComposesPerLeafAlternative")]
        public void Shark_CallbackRequirement_RawGraphEditsComposeWithUnrelatedLiveChanges(
            [Values("J", "W")] string format,
            [Values(false, true)] bool rawBeforeGetter,
            [Values("set", "deep-set", "remove", "replace", "null", "append")] string edit)
            => AssertRawGraphEditsCompose(format, rawBeforeGetter, edit, useOverlay: false);

        [Test]
        [Category("ComposesOverlayRequirement")]
        public void Shark_OverlayRequirement_RawGraphEditsComposeWithUnrelatedLiveChanges(
            [Values("J", "W")] string format,
            [Values(false, true)] bool rawBeforeGetter,
            [Values("set", "deep-set", "remove", "replace", "null", "append")] string edit)
            => AssertRawGraphEditsCompose(format, rawBeforeGetter, edit, useOverlay: true);

        private static void AssertRawGraphEditsCompose(string format, bool rawBeforeGetter, string edit, bool useOverlay)
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("{\"name\":\"mako\",\"finCount\":4,\"profile\":" + ReadProfileJson() + "}"),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            if (!rawBeforeGetter)
            {
                _ = shark.Profile;
            }
            switch (edit)
            {
                case "set":
                    shark.Patch.Set("$.profile.requiredInt"u8, "\"99\""u8);
                    break;
                case "deep-set":
                    shark.Patch.Set("$.profile.requiredRecordUnknown.rawOnly"u8, "\"raw\""u8);
                    break;
                case "remove":
                    shark.Patch.Remove("$.profile.requiredModel.requiredNullableString"u8);
                    break;
                case "replace":
                    shark.Patch.Set("$.profile.requiredDictionary"u8, """{"replacement":"raw"}"""u8);
                    break;
                case "null":
                    shark.Patch.SetNull("$.profile.requiredModel.optionalNullableList"u8);
                    break;
                case "append":
                    shark.Patch.Append("$.profile.requiredModel.requiredNullableList"u8, 4);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(edit), edit, "Unknown raw graph edit.");
            }
            var profile = shark.Profile;
            profile.RequiredString = "live";
            profile.RequiredModel.OptionalNullableString = "nested live";
            if (useOverlay)
            {
                UseLiveOverlayForComparison(shark);
            }
            string operations = shark.Patch.ToBinaryData().ToString();
            var options = new ModelReaderWriterOptions(format);
            string first = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            string second = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();

            using var document = JsonDocument.Parse(second);
            var written = document.RootElement.GetProperty("profile");
            var nested = written.GetProperty("requiredModel");
            Assert.Multiple(() =>
            {
                Assert.That(GetRootPropertyCount(document.RootElement, "profile"), Is.EqualTo(1));
                Assert.That(shark.Profile, Is.SameAs(profile));
                Assert.That(second, Is.EqualTo(first), "Repeated serialization must not duplicate raw appends.");
                Assert.That(written.GetProperty("requiredString").GetString(), Is.EqualTo("live"),
                    "An unrelated raw edit must not discard a live modeled value.");
                bool hasNestedMutation = nested.TryGetProperty("optionalNullableString", out var nestedMutation);
                Assert.That(hasNestedMutation, Is.True, "An unrelated raw edit must not discard a nested CLR mutation.");
                if (hasNestedMutation)
                {
                    Assert.That(nestedMutation.GetString(), Is.EqualTo("nested live"));
                }
                Assert.That(written.GetProperty("extra").GetString(), Is.EqualTo("stuff"));
                switch (edit)
                {
                    case "set":
                        Assert.That(written.GetProperty("requiredInt").GetString(), Is.EqualTo("99"));
                        break;
                    case "deep-set":
                        Assert.That(written.GetProperty("requiredRecordUnknown").GetProperty("rawOnly").GetString(), Is.EqualTo("raw"));
                        break;
                    case "remove":
                        Assert.That(nested.TryGetProperty("requiredNullableString", out _), Is.False,
                            "A modeled field removed through the raw patch must not be restored.");
                        break;
                    case "replace":
                        var dictionary = written.GetProperty("requiredDictionary");
                        Assert.That(dictionary.EnumerateObject().Select(x => x.Name), Is.EqualTo(new[] { "replacement" }),
                            "Whole-object replacement is not a union with the old modeled dictionary.");
                        Assert.That(dictionary.GetProperty("replacement").GetString(), Is.EqualTo("raw"));
                        break;
                    case "null":
                        Assert.That(nested.GetProperty("optionalNullableList").ValueKind, Is.EqualTo(JsonValueKind.Null));
                        break;
                    case "append":
                        Assert.That(nested.GetProperty("requiredNullableList").EnumerateArray().Select(x => x.GetInt32()),
                            Is.EqualTo(new[] { 1, 2, 3, 4 }));
                        break;
                }
                if (!useOverlay)
                {
                    Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(operations), "The default writer must not mutate Patch.");
                }
            });
        }

        [Test]
        [Category("ComposesOverlayComparison")]
        public void Shark_OverlayCharacterization_DeletedModeledFieldReturnsAndSerializationMutatesPatch()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("""{"name":"mako","finCount":4,"tracker":{"bar":"service","unknown":"kept"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            _ = shark.Tracker;
            // This comparison requires an explicit competing anchor, not seed-only received data.
            shark.Patch.Set("$.tracker"u8, """{"bar":"service","unknown":"kept"}"""u8);
            shark.Patch.Remove("$.tracker.bar"u8);
            string before = shark.Patch.ToBinaryData().ToString();
            UseLiveOverlayForComparison(shark);

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));

            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("service"),
                "Limitation only: the opt-in overlay restores the modeled raw-deleted property.");
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.Not.EqualTo(before));
        }

        [TestCase("""{"bar":"raw","rawOnly":"kept"}""")]
        [TestCase("null")]
        [Category("ComposesOverlayRequirement")]
        public void Shark_OverlayRequirement_WholeMemberOverrideRemainsAuthoritative(string raw)
        {
            var shark = new MyShark("mako", 4) { Tracker = new TrackerReference("live") };
            shark.Patch.Set("$.tracker"u8, BinaryData.FromString(raw));
            UseLiveOverlayForComparison(shark);

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));

            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("tracker").GetRawText(), Is.EqualTo(raw),
                "A comparison that preserves raw-only additions still has to respect whole-member overrides.");
        }

        [Test]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_ScopedAppendUpdatesBothTheMemberSnapshotAndPayload()
        {
            var shark = ModelReaderWriter.Read<MyShark>(
                BinaryData.FromString("{\"name\":\"mako\",\"finCount\":4,\"profile\":" + ReadProfileJson() + "}"),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            shark.Patch.Append("$.profile.requiredModel.requiredNullableList"u8, 4);

            using var snapshot = JsonDocument.Parse(shark.Patch.GetJson("$.profile"u8).ToArray());
            using var payload = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            TestContext.Out.WriteLine("Operations: " + shark.Patch.ToBinaryData());
            TestContext.Out.WriteLine("Payload: " + payload.RootElement.GetRawText());

            Assert.That(snapshot.RootElement.GetProperty("requiredModel").GetProperty("requiredNullableList").EnumerateArray().Select(x => x.GetInt32()),
                Is.EqualTo(new[] { 1, 2, 3, 4 }),
                "The target now publishes the scoped append as a complete explicit member.");
            Assert.That(payload.RootElement.GetProperty("profile").GetProperty("requiredModel").GetProperty("requiredNullableList").EnumerateArray().Select(x => x.GetInt32()),
                Is.EqualTo(new[] { 1, 2, 3, 4 }),
                "The scoped raw operation must remain nested on the wire.");
            Assert.That(payload.RootElement.TryGetProperty("requiredNullableList", out _), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesOverlayComparison")]
        public void Shark_OverlayCharacterization_ReassignmentOrClearCanRetainOldUnknowns(bool clear)
        {
            var shark = new MyShark("mako", 4) { Tracker = new TrackerReference("old") };
            shark.Patch.Set("$.tracker.oldUnknown"u8, "\"old\""u8);
            UseLiveOverlayForComparison(shark);
            _ = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);
            var orphan = shark.Tracker;
            shark.Tracker = clear ? null : new TrackerReference("replacement");
            orphan.Bar = "orphan mutation";

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            var tracker = document.RootElement.GetProperty("tracker");
            Assert.That(tracker.GetProperty("oldUnknown").GetString(), Is.EqualTo("old"),
                "Limitation only: previously merged unknown data survives replacement or clearing.");
            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo(clear ? "old" : "replacement"));
            Assert.That(shark.Tracker, clear ? Is.Null : Is.Not.SameAs(orphan));
        }

        [Test]
        [Category("ComposesCallbackCharacterization")]
        public void JsonPatch_LatePropagatorsDoNotRecoverEarlierPaths()
        {
            var patch = new JsonPatch();
            patch.Set("$.tracker.before"u8, "\"raw\""u8);
            var observed = new List<string>();
            patch.SetPropagators(
                (ReadOnlySpan<byte> path, JsonPatch.EncodedValue _) =>
                {
                    observed.Add(Encoding.UTF8.GetString(path));
                    return false;
                },
                (ReadOnlySpan<byte> path, out JsonPatch.EncodedValue value) =>
                {
                    value = default;
                    return false;
                });
            patch.Set("$.tracker.after"u8, "\"raw\""u8);

            Assert.That(observed, Is.EqualTo(new[] { "$.tracker.after" }));
            using var operations = JsonDocument.Parse(patch.ToBinaryData());
            Assert.That(operations.RootElement[0].GetProperty("path").GetString(), Is.EqualTo("/tracker"),
                "Installing a propagator at first extension access does not recover prior descendant edit intent.");
        }

        private static bool s_lateLibraryInitialized;

        private static class LateComposingLibrary
        {
            private static readonly ConditionalWeakTable<MyShark, TrackerReference> Values = new();

            static LateComposingLibrary()
            {
                MyShark.AddComposedWriter(static (writer, target, _) =>
                {
                    if (target.Patch.Contains("$.late"u8))
                    {
                        return;
                    }
                    if (Values.TryGetValue(target, out var child))
                    {
                        writer.WritePropertyName("late");
                        ((IJsonModel<TrackerReference>)child).Write(writer, ModelReaderWriterOptions.Json);
                    }
                    else if (target.Patch.TryGetJson("$.late"u8, out var received))
                    {
                        writer.WritePropertyName("late");
                        writer.WriteRawValue(received.Span);
                    }
                }, "late");
                s_lateLibraryInitialized = true;
            }

            public static void Set(MyShark target, TrackerReference child) => Values.Add(target, child);
        }

        private static string ReadProfileJson() => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "ModelReaderWriterValidation", "TestProjects", "Sample_TypeSpec",
            "TestData", "RoundTripModel", "RoundTripModel.json"));

        private static void UseLiveOverlayForComparison(MyShark shark)
        {
            var method = typeof(MySharkExtensions).GetMethod(
                "UseLiveOverlayForComparison", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "The comparison must remain internal, never a public SDK switch.");
            method!.Invoke(null, new object[] { shark });
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

        /// <summary>
        /// Counts raw occurrences of a property name in the JSON text.
        /// <para>
        /// <see cref="JsonDocument"/> silently keeps only one value for a duplicated key, so a
        /// DOM-based assertion cannot see duplicate-key corruption at all.
        /// </para>
        /// </summary>
        private static int CountRawOccurrences(string json, string propertyName)
        {
            string needle = $"\"{propertyName}\":";
            int count = 0;
            int index = 0;
            while ((index = json.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}
