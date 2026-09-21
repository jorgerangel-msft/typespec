// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.ClientModel.Primitives;
using System.Text.Json;
using NUnit.Framework;
using SampleTypeSpec;

namespace Microsoft.TypeSpec.Generator.ClientModel.Tests.ModelReaderWriterValidation.TestProjects.Sample_TypeSpec
{
    internal partial class ComposedModelsTests
    {
#pragma warning disable SCME0001

        [Test]
        [Category("ComposesFormatPolicy")]
        public void ComposedFormat_JsonPolicyPreservesRawOrLiveChildDataRegardlessOfParentFormat(
            [Values("shark", "dynamic", "nullable", "derived")] string targetKind,
            [Values("J", "W")] string readFormat, [Values(false, true)] bool streamingReader)
        {
            string source = ReadProfileJson();
            var expectedChild = ModelReaderWriter.Read<RoundTripModel>(
                BinaryData.FromString(source), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            using var expectedJson = JsonDocument.Parse(ModelReaderWriter.Write(
                expectedChild, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            using var expectedWire = JsonDocument.Parse(ModelReaderWriter.Write(
                expectedChild, new ModelReaderWriterOptions("W"), SampleTypeSpecContext.Default));
            Assert.That(expectedJson.RootElement.TryGetProperty("readOnlyRequiredRecordUnknown", out _), Is.True);
            Assert.That(expectedJson.RootElement.TryGetProperty("extra", out _), Is.True);
            Assert.That(expectedWire.RootElement.TryGetProperty("readOnlyRequiredRecordUnknown", out _), Is.False);
            Assert.That(expectedWire.RootElement.TryGetProperty("extra", out _), Is.False);

            foreach (string access in new[] { "assigned", "untouched", "getter" })
            {
                object target = ReadFormatTarget(targetKind, "profile", source, readFormat, streamingReader);
                RoundTripModel? retained = null;
                if (access == "assigned")
                {
                    retained = expectedChild;
                    SetFormatProfile(target, retained);
                }
                else if (access == "getter")
                {
                    retained = GetFormatProfile(target);
                }

                // W first must not discard data from the JSON-backed materialization used by a later J write.
                foreach (string outputFormat in new[] { "W", "J", "W" })
                {
                    using var actual = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions(outputFormat)));
                    Assert.That(GetRootPropertyCount(actual.RootElement, "profile"), Is.EqualTo(1), access);
                    Assert.That(JsonElement.DeepEquals(actual.RootElement.GetProperty("profile"), expectedJson.RootElement), Is.True,
                        $"{targetKind}, {readFormat} -> {outputFormat}, {access}: preserve JSON data, including read-only/SARD fields.");
                    retained ??= GetFormatProfile(target);
                    Assert.That(GetFormatProfile(target), Is.SameAs(retained));
                }
            }
        }

        [TestCase("shark")]
        [TestCase("dynamic")]
        [TestCase("nullable")]
        [TestCase("derived")]
        [Category("ComposesFormatPolicy")]
        public void ComposedFormat_ReceivedNullAndAssignedNullRemainDistinct(string targetKind)
        {
            object target = ReadFormatTarget(targetKind, "profile", "null", "W", streaming: true);
            Assert.That(FormatPatch(target).Contains("$.profile"u8), Is.False);
            Assert.That(FormatPatch(target).TryGetJson("$.profile"u8, out var sourceNull), Is.True);
            Assert.That(BinaryData.FromBytes(sourceNull).ToString(), Is.EqualTo("null"));
            foreach (string format in new[] { "J", "W" })
            {
                using var untouched = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions(format)));
                Assert.That(untouched.RootElement.GetProperty("profile").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
            Assert.That(GetFormatProfile(target), Is.Null);
            using (var readNull = JsonDocument.Parse(WriteFormatTarget(target, ModelReaderWriterOptions.Json)))
            {
                Assert.That(readNull.RootElement.GetProperty("profile").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
            SetFormatProfile(target, null!);
            foreach (string format in new[] { "J", "W" })
            {
                using var assigned = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions(format)));
                Assert.That(assigned.RootElement.TryGetProperty("profile", out _), Is.False);
            }
        }

        [TestCase("shark")]
        [TestCase("dynamic")]
        [TestCase("nullable")]
        [TestCase("derived")]
        [Category("ComposesFormatPolicy")]
        public void ComposedFormat_ExplicitRawValuesBypassTypedMaterializationAndOutputFiltering(string targetKind)
        {
            const string invalidChild = """{"bar":123}""";
            const string raw = """{ "bar": "raw", "unknown": { "keep": true } }""";
            object target = ReadFormatTarget(targetKind, "tracker", invalidChild, "W", streaming: true);
            FormatPatch(target).Set("$.tracker"u8, BinaryData.FromString(raw));
            foreach (string format in new[] { "J", "W" })
            {
                using var document = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions(format)));
                Assert.That(document.RootElement.GetProperty("tracker").GetRawText(), Is.EqualTo(raw));
                Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            }
            object removed = ReadFormatTarget(targetKind, "tracker", invalidChild, "W", streaming: true);
            FormatPatch(removed).Remove("$.tracker"u8);
            using var omitted = JsonDocument.Parse(WriteFormatTarget(removed, new ModelReaderWriterOptions("W")));
            Assert.That(omitted.RootElement.TryGetProperty("tracker", out _), Is.False,
                "An invalid typed source must not be materialized when an explicit override or tombstone owns it.");
        }

        [Test]
        [Category("ComposesRawSeedPolicy")]
        public void ComposedRaw_ExistingSeedAndCurrentOverrideDoNotRequireSardMembership(
            [Values("shark", "dynamic", "nullable", "derived")] string targetKind,
            [Values("J", "W")] string readFormat, [Values(false, true)] bool streamingReader)
        {
            const string source = """{"one":1,"toRemove":true,"nested":{"flag":false},"items":[1,2]}""";
            object target = ReadFormatTarget(targetKind, "rawObject", source, readFormat, streamingReader);
            Assert.That(GetComposedAdditionalData(target).ContainsKey("rawObject"), Is.EqualTo(readFormat != "W"));
            Assert.That(FormatPatch(target).Contains("$.rawObject"u8), Is.False);
            Assert.That(FormatPatch(target).TryGetJson("$.rawObject"u8, out _), Is.True);

            FormatPatch(target).Set("$.rawObject.one"u8, 2);
            FormatPatch(target).Set("$.rawObject.nested.flag"u8, true);
            FormatPatch(target).Remove("$.rawObject.toRemove"u8);
            FormatPatch(target).SetNull("$.rawObject.explicitNull"u8);
            FormatPatch(target).Append("$.rawObject.items"u8, 3);
            const string expected = """{"one":2,"nested":{"flag":true},"items":[1,2,3],"explicitNull":null}""";
            using var expectedDocument = JsonDocument.Parse(expected);
            foreach (string format in new[] { "J", "W" })
            {
                using var document = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions(format)));
                Assert.That(GetRootPropertyCount(document.RootElement, "rawObject"), Is.EqualTo(1));
                Assert.That(JsonElement.DeepEquals(document.RootElement.GetProperty("rawObject"), expectedDocument.RootElement), Is.True);
                Assert.That(document.RootElement.TryGetProperty("one", out _), Is.False);
                Assert.That(document.RootElement.TryGetProperty("items", out _), Is.False);
            }

            string before = FormatPatch(target).ToBinaryData().ToString();
            Assert.Catch<JsonException>(() => FormatPatch(target).Set("$.rawObject.invalid"u8, "{"u8));
            Assert.That(FormatPatch(target).ToBinaryData().ToString(), Is.EqualTo(before));
            FormatPatch(target).Set("$.rawObject"u8, """{"items":[8]}"""u8);
            FormatPatch(target).Append("$.rawObject.items"u8, 9);
            using (var replaced = JsonDocument.Parse(WriteFormatTarget(target, ModelReaderWriterOptions.Json)))
            {
                Assert.That(replaced.RootElement.GetProperty("rawObject").GetRawText(), Is.EqualTo("""{"items":[8,9]}"""));
            }
            FormatPatch(target).Remove("$.rawObject"u8);
            FormatPatch(target).Set("$.rawObject.one"u8, 10);
            using var removed = JsonDocument.Parse(WriteFormatTarget(target, ModelReaderWriterOptions.Json));
            Assert.That(removed.RootElement.TryGetProperty("rawObject", out _), Is.False, "Do not revive the received source after removal.");
        }

        [Test]
        [Category("ComposesRawSeedPolicy")]
        public void ComposedRaw_SeededArrayEditsAndFailureAtomicityDoNotRequireSardMembership(
            [Values("shark", "dynamic", "nullable", "derived")] string targetKind,
            [Values("J", "W")] string readFormat, [Values(false, true)] bool streamingReader)
        {
            object target = ReadFormatTarget(targetKind, "rawArray", "[1,2]", readFormat, streamingReader);
            FormatPatch(target).Append("$.rawArray"u8, 3);
            FormatPatch(target).AppendNull("$.rawArray"u8);
            using (var written = JsonDocument.Parse(WriteFormatTarget(target, new ModelReaderWriterOptions("W"))))
            {
                Assert.That(GetRootPropertyCount(written.RootElement, "rawArray"), Is.EqualTo(1));
                Assert.That(written.RootElement.GetProperty("rawArray").GetRawText(), Is.EqualTo("[1,2,3,null]"));
            }
            FormatPatch(target).SetNull("$.rawArray"u8);
            string before = FormatPatch(target).ToBinaryData().ToString();
            Assert.Catch<JsonException>(() => FormatPatch(target).Append("$.rawArray"u8, 4));
            Assert.That(FormatPatch(target).ToBinaryData().ToString(), Is.EqualTo(before));
            using (var unchanged = JsonDocument.Parse(WriteFormatTarget(target, ModelReaderWriterOptions.Json)))
            {
                Assert.That(unchanged.RootElement.GetProperty("rawArray").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
            FormatPatch(target).Set("$.rawArray"u8, "[9]"u8);
            FormatPatch(target).Append("$.rawArray"u8, 10);
            using var replacement = JsonDocument.Parse(WriteFormatTarget(target, ModelReaderWriterOptions.Json));
            Assert.That(replacement.RootElement.GetProperty("rawArray").GetRawText(), Is.EqualTo("[9,10]"));
        }

        private static object ReadFormatTarget(string kind, string memberName, string memberJson, string format, bool streaming)
        {
            // Register writers without reading a composed value from the target under test.
            if (kind == "shark")
                _ = new MyShark("registration", 0).Profile;
            else
                _ = new NullableDynamicModel().Profile;
            using var original = JsonDocument.Parse(kind == "dynamic" ? MixedDynamicJson
                : kind == "shark" ? """{"name":"mako","finCount":4}""" : MixedNullableJson);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in original.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals(memberName))
                        property.WriteTo(writer);
                }
                writer.WritePropertyName(memberName);
                writer.WriteRawValue(memberJson);
                if (kind == "derived")
                {
                    writer.WritePropertyName("derivedChild");
                    writer.WriteRawValue("""{"bar":"derived"}""");
                }
                writer.WriteEndObject();
            }
            var data = BinaryData.FromBytes(buffer.WrittenMemory);
            var options = new ModelReaderWriterOptions(format);
            var reader = new Utf8JsonReader(data.ToMemory().Span);
            switch (kind)
            {
                case "shark":
                    return ReadSourceShark(data.ToString(), options, streaming);
                case "dynamic":
                    return streaming
                        ? ((IJsonModel<DynamicModel>)Activator.CreateInstance(typeof(DynamicModel), nonPublic: true)!).Create(ref reader, options)!
                        : ModelReaderWriter.Read<DynamicModel>(data, options, SampleTypeSpecContext.Default)!;
                case "nullable":
                    return streaming
                        ? ((IJsonModel<NullableDynamicModel>)new NullableDynamicModel()).Create(ref reader, options)!
                        : ModelReaderWriter.Read<NullableDynamicModel>(data, options, SampleTypeSpecContext.Default)!;
                case "derived":
                    var prototype = CreateMixedDerived();
                    return streaming
                        ? ((IJsonModel<NullableDynamicModel>)prototype).Create(ref reader, options)!
                        : ((IPersistableModel<NullableDynamicModel>)prototype).Create(data, options)!;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static ref JsonPatch FormatPatch(object target)
        {
            if (target is MyShark shark)
                return ref shark.Patch;
            return ref MixedPatch(target);
        }

        private static RoundTripModel GetFormatProfile(object target) => target switch
        {
            MyShark shark => shark.Profile,
            DynamicModel dynamicTarget => dynamicTarget.Profile,
            NullableDynamicModel nullableTarget => nullableTarget.Profile,
            _ => throw new ArgumentException("Unexpected target.", nameof(target)),
        };

        private static void SetFormatProfile(object target, RoundTripModel profile)
        {
            switch (target)
            {
                case MyShark shark:
                    shark.Profile = profile;
                    break;
                case DynamicModel dynamicTarget:
                    dynamicTarget.Profile = profile;
                    break;
                case NullableDynamicModel nullableTarget:
                    nullableTarget.Profile = profile;
                    break;
                default:
                    throw new ArgumentException("Unexpected target.", nameof(target));
            }
        }

        private static BinaryData WriteFormatTarget(object target, ModelReaderWriterOptions options)
            => target is MyShark shark ? ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default)
                : target is NullableDynamicModel nullable ? WriteMixedDerived(nullable, options)
                : WriteMixedTarget(target, options);

#pragma warning restore SCME0001
    }
}
