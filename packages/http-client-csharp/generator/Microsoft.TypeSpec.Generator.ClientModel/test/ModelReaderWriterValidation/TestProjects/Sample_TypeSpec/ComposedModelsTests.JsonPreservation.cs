// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SampleTypeSpec;

namespace Microsoft.TypeSpec.Generator.ClientModel.Tests.ModelReaderWriterValidation.TestProjects.Sample_TypeSpec
{
    internal partial class ComposedModelsTests
    {
#pragma warning disable SCME0001

        [Test]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_UntouchedRegisteredAndUnclaimedValuesSurviveWithoutMaterialization(
            [Values("shark", "dynamic", "nullable", "derived")] string kind,
            [Values("J", "W")] string inputFormat, [Values(false, true)] bool streaming)
        {
            string profileJson = ReadProfileJson();
            object target = ReadFormatTarget(kind, "profile", profileJson, inputFormat, streaming);
            FormatPatch(target).Set("$.tag"u8, "explicit scalar");
            using var sourceProfile = JsonDocument.Parse(profileJson);
            foreach (string outputFormat in new[] { "W", "J" })
            {
                using var output = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions(outputFormat)));
                var root = output.RootElement;
                Assert.That(JsonElement.DeepEquals(root.GetProperty("profile"), sourceProfile.RootElement), Is.True);
                Assert.That(root.GetProperty("tag").GetString(), Is.EqualTo("explicit scalar"));
                Assert.That(GetRootPropertyCount(root, "profile"), Is.EqualTo(1));
                Assert.That(ProfileIsInitialized(target), Is.False, "Registration and writing alone must not materialize untouched children.");
                if (kind != "shark")
                {
                    Assert.That(root.GetProperty("future").GetProperty("keep").GetInt32(), Is.EqualTo(1),
                        "The target preserves unclaimed raw values even when W-read SARD is empty.");
                }
            }
            Assert.That(GetComposedAdditionalData(target).Count == 0, Is.EqualTo(inputFormat == "W"));
        }

        [Test]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_GetterReferenceAndNestedMutationsAreLiveWithoutTheExtensionSetter(
            [Values("shark", "dynamic", "nullable", "derived")] string kind, [Values("J", "W")] string format)
        {
            object target = ReadFormatTarget(kind, "profile", ReadProfileJson(), format, streaming: true);
            RoundTripModel profile = GetFormatProfile(target);
            var nested = profile.RequiredModel;
            var list = nested.RequiredNullableList;
            var dictionary = profile.RequiredRecordUnknown;
            Assert.That(GetFormatProfile(target), Is.SameAs(profile));
            Assert.That(GetFormatProfile(target).RequiredModel, Is.SameAs(nested));
            nested.OptionalNullableString = "nested mutation";
            list[0] = 7;
            list.Add(99);
            list.RemoveAt(1);
            dictionary["recordKey1"] = BinaryData.FromString("\"dictionary mutation\"");
            dictionary.Remove("recordKey2");
            dictionary["added"] = BinaryData.FromString("""{"nested":"value"}""");
            var expected = ModelReaderWriter.Write(profile, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default);

            using (var actual = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions("W"))))
            using (var expectedDocument = JsonDocument.Parse(expected))
            {
                var member = actual.RootElement.GetProperty("profile");
                Assert.That(JsonElement.DeepEquals(member, expectedDocument.RootElement), Is.True);
                Assert.That(member.GetProperty("requiredModel").GetProperty("optionalNullableString").GetString(), Is.EqualTo("nested mutation"));
                Assert.That(member.GetProperty("requiredModel").GetProperty("requiredNullableList").EnumerateArray().Select(x => x.GetInt32()),
                    Is.EqualTo(new[] { 7, 3, 99 }));
                Assert.That(member.GetProperty("requiredRecordUnknown").GetProperty("recordKey1").GetString(), Is.EqualTo("dictionary mutation"));
                Assert.That(member.GetProperty("readOnlyRequiredRecordUnknown").GetProperty("recordKey1").GetString(), Is.EqualTo("foo"));
                Assert.That(member.GetProperty("extra").GetString(), Is.EqualTo("stuff"));
            }
            var replacement = ModelReaderWriter.Read<RoundTripModel>(
                BinaryData.FromString(ReadProfileJson()), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!.RequiredModel;
            replacement.OptionalNullableString = "replacement";
            profile.RequiredModel = replacement;
            nested.OptionalNullableString = "orphan change";
            replacement.RequiredNullableList = new List<int> { 11, 12 };
            list.Add(13);
            using var next = JsonDocument.Parse(WriteFormatTarget(target, ModelReaderWriterOptions.Json));
            Assert.That(next.RootElement.GetProperty("profile").GetProperty("requiredModel").GetProperty("optionalNullableString").GetString(),
                Is.EqualTo("replacement"));
            Assert.That(next.RootElement.GetProperty("profile").GetProperty("requiredModel").GetProperty("requiredNullableList").GetArrayLength(),
                Is.EqualTo(2), "Replacing a nested collection must not rebind a previously returned alias.");
            Assert.That(GetFormatProfile(target), Is.SameAs(profile));
            Assert.That(FormatPatch(target).Contains("$.profile"u8), Is.False,
                "A child's mutation must not require or synthesize an extension setter/parent snapshot.");
        }

        [Test]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_SharedProfileKeepsAliasesAndDetachedReferences()
        {
            object first = ReadFormatTarget("shark", "profile", ReadProfileJson(), "W", true);
            object second = ReadFormatTarget("nullable", "profile", "null", "W", true);
            var shared = GetFormatProfile(first);
            SetFormatProfile(second, shared);
            shared.RequiredModel.OptionalNullableString = "shared";
            foreach (var target in new[] { first, second })
            {
                using var output = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions("W")));
                Assert.That(output.RootElement.GetProperty("profile").GetProperty("requiredModel").GetProperty("optionalNullableString").GetString(),
                    Is.EqualTo("shared"));
                Assert.That(GetFormatProfile(target), Is.SameAs(shared));
            }
            var replacement = ModelReaderWriter.Read<RoundTripModel>(
                BinaryData.FromString(ReadProfileJson()), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            SetFormatProfile(first, replacement);
            shared.RequiredModel.OptionalNullableString = "old reference";
            using var firstOutput = JsonDocument.Parse(WriteFormatTarget(first, ModelReaderWriterOptions.Json));
            using var secondOutput = JsonDocument.Parse(WriteFormatTarget(second, ModelReaderWriterOptions.Json));
            Assert.That(firstOutput.RootElement.GetProperty("profile").GetProperty("requiredModel").TryGetProperty("optionalNullableString", out _), Is.False);
            Assert.That(secondOutput.RootElement.GetProperty("profile").GetProperty("requiredModel").GetProperty("optionalNullableString").GetString(),
                Is.EqualTo("old reference"));
        }

        [TestCase("{}")]
        [TestCase("""{"name":"source","finCount":4,"future":{"keep":1}}""")]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_AddThenRemoveDoesNotMakeTheEnclosingWriterDependOnBrokenStandaloneProjection(string source)
        {
            var shark = ReadSourceShark(source, new ModelReaderWriterOptions("W"), streamingReader: true);
            shark.Name = "live";
            shark.Patch.Set("$.added"u8, 1);
            shark.Patch.Remove("$.added"u8);
            using var output = JsonDocument.Parse(ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default));
            Assert.That(output.RootElement.GetProperty("name").GetString(), Is.EqualTo("live"));
            Assert.That(output.RootElement.TryGetProperty("added", out _), Is.False);
            if (source != "{}")
            {
                Assert.That(output.RootElement.GetProperty("future").GetProperty("keep").GetInt32(), Is.EqualTo(1));
            }
        }

        [Test]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_FallbackUsesTheReadersSupportedJsonDepth()
        {
            string nested = new string('[', 80) + "1" + new string(']', 80);
            var data = BinaryData.FromString("{\"name\":\"source\",\"finCount\":4,\"deepUnknown\":" + nested + "}");
            var shark = ((IPersistableModel<MyShark>)new MyShark("reader", 0)).Create(data, new ModelReaderWriterOptions("W"))!;
            shark.Name = "changed";
            var written = ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default);
            using var document = JsonDocument.Parse(written, new JsonDocumentOptions { MaxDepth = 256 });
            Assert.That(document.RootElement.GetProperty("name").GetString(), Is.EqualTo("changed"));
            Assert.That(document.RootElement.GetProperty("deepUnknown").GetRawText(), Is.EqualTo(nested));
        }

        [TestCase("nullable")]
        [TestCase("derived")]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_OriginalDeclaredNullsAndDerivedValuesAreNeverWrittenAsUnknownFallback(string kind)
        {
            object target = ReadFormatTarget(kind, "modelValue", """{"bar":"old"}""", "W", true);
            var model = (NullableDynamicModel)target;
            model.ModelValue = null!;
            if (kind == "derived")
            {
                model.GetType().GetProperty("DerivedChild")!.SetValue(model, null);
            }
            using var output = JsonDocument.Parse(WriteFormatTarget(model, new ModelReaderWriterOptions("W")));
            Assert.That(output.RootElement.TryGetProperty("modelValue", out _), Is.False);
            Assert.That(output.RootElement.TryGetProperty("derivedChild", out _), Is.False);
            Assert.That(output.RootElement.GetProperty("future").GetProperty("keep").GetInt32(), Is.EqualTo(1));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_ClearedOptionalDeclaredPropertiesDoNotReappear(string format)
        {
            string source = MixedDynamicJson.TrimEnd()[..^1] + ",\"optionalUnknown\":{\"old\":true},\"optionalInt\":42}";
            var model = ModelReaderWriter.Read<DynamicModel>(
                BinaryData.FromString(source), new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default)!;
            model.OptionalUnknown = null!;
            model.OptionalInt = null;
            foreach (string outputFormat in new[] { "W", "J" })
            {
                using var output = JsonDocument.Parse(ModelReaderWriter.Write(
                    model, new ModelReaderWriterOptions(outputFormat), SampleTypeSpecContext.Default));
                Assert.That(output.RootElement.TryGetProperty("optionalUnknown", out _), Is.False);
                Assert.That(output.RootElement.TryGetProperty("optionalInt", out _), Is.False);
                Assert.That(output.RootElement.GetProperty("future").GetProperty("keep").GetInt32(), Is.EqualTo(1));
            }
        }

        [Test]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_GetterOnlyUsesGeneratedJsonNormalizationNotArbitraryByteIdentity()
        {
            var shark = ReadSourceShark("""{"name":"mako","finCount":4,"tracker":{}}""", new ModelReaderWriterOptions("W"), true);
            _ = new MyShark("registration", 0).Tracker;
            using (var untouched = JsonDocument.Parse(ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default)))
            {
                Assert.That(untouched.RootElement.GetProperty("tracker").EnumerateObject(), Is.Empty);
            }
            var tracker = shark.Tracker;
            Assert.That(tracker.Bar, Is.Null);
            using var materialized = JsonDocument.Parse(ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default));
            using var expected = JsonDocument.Parse(ModelReaderWriter.Write(tracker, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(materialized.RootElement.GetProperty("tracker").GetProperty("bar").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "The actual generated serializer inserts a missing required model field as null. This normalization is explicit.");
            Assert.That(JsonElement.DeepEquals(materialized.RootElement.GetProperty("tracker"), expected.RootElement), Is.True);
        }

        [TestCase("shark")]
        [TestCase("dynamic")]
        [TestCase("nullable")]
        [TestCase("derived")]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_OriginalInputReusesPersistedMemoryAndDerivedConstructorBinding(string kind)
        {
            string json = kind == "shark" ? ReceivedSource
                : kind == "dynamic" ? MixedDynamicJson
                : MixedNullableJson.TrimEnd()[..^1] + ",\"derivedChild\":{\"bar\":\"declared\"}}";
            var data = BinaryData.FromString(json);
            var options = new ModelReaderWriterOptions("W");
            object target = kind switch
            {
                "shark" => ((IPersistableModel<MyShark>)new MyShark("reader", 0)).Create(data, options)!,
                "dynamic" => ((IPersistableModel<DynamicModel>)Activator.CreateInstance(typeof(DynamicModel), nonPublic: true)!).Create(data, options)!,
                "nullable" => ((IPersistableModel<NullableDynamicModel>)new NullableDynamicModel()).Create(data, options)!,
                "derived" => ((IPersistableModel<NullableDynamicModel>)CreateMixedDerived()).Create(data, options)!,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var declaringType = target is MyShark ? typeof(MyShark)
                : target is DynamicModel ? typeof(DynamicModel) : typeof(NullableDynamicModel);
            var original = (ReadOnlyMemory<byte>)declaringType.GetField(
                "_originalInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
            Assert.That(original.Equals(data.ToMemory()), Is.True, "The target must retain the existing memory, not allocate another JSON copy.");
            Assert.That(GetComposedAdditionalData(target), Is.Empty, "Sharing the source must not replace the non-W SARD policy.");
            object patch = FormatPatch(target);
            var setter = (Delegate)typeof(JsonPatch).GetField("_propagatorSetter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(patch)!;
            Assert.That(setter.Target, Is.SameAs(target), "A derived/full constructor must rebind the patch to the resulting owner.");
        }

        [Test]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_FullConstructorWithoutOriginalInputRetainsItsAdditionalDataFallback()
        {
            var constructor = typeof(MyShark).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 5);
            var data = new Dictionary<string, BinaryData> { ["future"] = BinaryData.FromString("""{"keep":1}""") };
            var shark = (MyShark)constructor.Invoke(
                new object[] { "factory", 4, default(JsonPatch), data, default(ReadOnlyMemory<byte>) });
            using (var output = JsonDocument.Parse(ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default)))
            {
                Assert.That(output.RootElement.GetProperty("future").GetProperty("keep").GetInt32(), Is.EqualTo(1));
            }
            shark.Patch.Remove("$.future"u8);
            using var removed = JsonDocument.Parse(ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(removed.RootElement.TryGetProperty("future", out _), Is.False);
        }

        [TestCase("")]
        [TestCase("dot.name")]
        [TestCase("bracket[name]")]
        [TestCase("single'quote")]
        [TestCase("double\"quote")]
        [TestCase("back\\slash")]
        [TestCase("\u00e9\u96ea")]
        [TestCase("literal\\u0041")]
        [TestCase("line\nbreak")]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_UnclaimedLiteralNamesRespectRawOwnership(string name)
        {
            string json = "{\"name\":\"mako\",\"finCount\":4," + JsonSerializer.Serialize(name) + ":{\"keep\":true}}";
            var shark = ReadSourceShark(json, new ModelReaderWriterOptions("W"), streamingReader: true);
            byte[] path = System.Text.Encoding.UTF8.GetBytes("$[" + JsonSerializer.Serialize(name) + "]");
            using (var original = JsonDocument.Parse(ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default)))
            {
                Assert.That(GetRootPropertyCount(original.RootElement, name), Is.EqualTo(1));
                Assert.That(original.RootElement.GetProperty(name).GetProperty("keep").GetBoolean(), Is.True);
            }
            shark.Patch.Set(path, """{"raw":true}"""u8);
            using (var overwritten = JsonDocument.Parse(ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)))
            {
                Assert.That(GetRootPropertyCount(overwritten.RootElement, name), Is.EqualTo(1));
                Assert.That(overwritten.RootElement.GetProperty(name).GetProperty("raw").GetBoolean(), Is.True);
                Assert.That(overwritten.RootElement.GetProperty(name).TryGetProperty("keep", out _), Is.False);
            }
            shark.Patch.Remove(path);
            using var removed = JsonDocument.Parse(ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(removed.RootElement.TryGetProperty(name, out _), Is.False);
        }

        [Test]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_RegistrationSnapshotDoesNotPrematurelyHideRawSource()
        {
            const string name = "jsonPreservationSnapshot";
            var shark = ReadSourceShark(
                """{"name":"snapshot-owner","finCount":4,"jsonPreservationSnapshot":{"nested":{"keep":true}}}""",
                new ModelReaderWriterOptions("W"), streamingReader: true);
            bool added = false;
            Action<System.Text.Json.Utf8JsonWriter, MyShark, ModelReaderWriterOptions> second = (writer, target, _) =>
            {
                if (ReferenceEquals(target, shark) && !target.Patch.Contains("$.jsonPreservationSnapshot"u8))
                {
                    writer.WritePropertyName(name);
                    writer.WriteRawValue(target.Patch.GetJson("$.jsonPreservationSnapshot"u8).ToMemory().Span);
                }
            };
            MyShark.AddComposedWriter((_, target, _) =>
            {
                if (ReferenceEquals(target, shark) && !added)
                {
                    added = true;
                    MyShark.AddComposedWriter(second, name);
                }
            });
            string first = ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default).ToString();
            string next = ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default).ToString();
            using var firstDocument = JsonDocument.Parse(first);
            using var nextDocument = JsonDocument.Parse(next);
            Assert.That(GetRootPropertyCount(firstDocument.RootElement, name), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(nextDocument.RootElement, name), Is.EqualTo(1));
            Assert.That(JsonElement.DeepEquals(firstDocument.RootElement, nextDocument.RootElement), Is.True,
                "Callbacks and fallback must use the same registration snapshot even if another writer is registered mid-write.");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesJsonPreservation")]
        public void JsonPreservation_RawOwnershipRemainsAuthoritativeOverOriginalSource(string format)
        {
            var shark = ReadSourceShark(
                """{"name":"service","finCount":4,"tracker":{"bar":"service","unknown":true},"tag":"source","rawArray":[1,2]}""",
                new ModelReaderWriterOptions(format), streamingReader: true);
            var retained = shark.Tracker;
            retained.Bar = "live but raw will win";
            shark.Patch.Set("$.tracker"u8, """{"bar":"service","unknown":true}"""u8);
            shark.Patch.Append("$.rawArray"u8, 3);
            shark.Patch.Set("$.rawArray[0]"u8, 9);
            shark.Patch.Remove("$.tag"u8);
            string operations = shark.Patch.ToBinaryData().ToString();
            foreach (string outputFormat in new[] { "W", "J", "W" })
            {
                using var output = JsonDocument.Parse(ModelReaderWriter.Write(
                    shark, new ModelReaderWriterOptions(outputFormat), SampleTypeSpecContext.Default));
                Assert.That(output.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("service"));
                Assert.That(output.RootElement.TryGetProperty("tag", out _), Is.False);
                Assert.That(output.RootElement.GetProperty("rawArray").EnumerateArray().Select(value => value.GetInt32()),
                    Is.EqualTo(new[] { 9, 2, 3 }));
                Assert.That(GetRootPropertyCount(output.RootElement, "tracker"), Is.EqualTo(1));
                Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(operations));
            }
            shark.Patch.Remove("$.tracker"u8);
            retained.Bar = "orphan";
            shark.Tracker = new TrackerReference("assignment cannot clear raw removal");
            using (var removed = JsonDocument.Parse(ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)))
            {
                Assert.That(removed.RootElement.TryGetProperty("tracker", out _), Is.False);
            }
            shark.Patch.SetNull("$.tracker"u8);
            using var rawNull = JsonDocument.Parse(ModelReaderWriter.Write(shark, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default));
            Assert.That(rawNull.RootElement.GetProperty("tracker").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        private static bool ProfileIsInitialized(object target)
        {
            object? state;
            if (target is MyShark)
            {
                object table = typeof(MySharkExtensions).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                object?[] args = [target, null];
                if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!)
                    return false;
                state = args[1];
            }
            else
            {
                Type modelType = target is DynamicModel ? typeof(DynamicModel) : typeof(NullableDynamicModel);
                var stateType = modelType.Assembly.GetType("SampleTypeSpec.MixedComposedState`1")!.MakeGenericType(modelType);
                object table = stateType.GetField("Table", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                object?[] args = [target, null];
                if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!)
                    return false;
                state = args[1];
            }
            object slot = state!.GetType().GetField("Profile")!.GetValue(state)!;
            return (bool)slot.GetType().GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot)!;
        }

#pragma warning restore SCME0001
    }
}
