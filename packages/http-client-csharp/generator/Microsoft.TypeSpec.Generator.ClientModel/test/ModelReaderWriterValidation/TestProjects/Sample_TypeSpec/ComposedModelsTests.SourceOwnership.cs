// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SampleTypeSpec;

namespace Microsoft.TypeSpec.Generator.ClientModel.Tests.ModelReaderWriterValidation.TestProjects.Sample_TypeSpec
{
    internal partial class ComposedModelsTests
    {
#pragma warning disable SCME0001

        private const string ReceivedSource = """
            {"name":"mako","finCount":4,"tracker":{"bar":"service","unknown":"kept"},"backupTracker":{"bar":"untouched","future":7},"tag":"received","future":{"nested":true}}
            """;

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_BothReadersPreserveUntouchedValuesAndSendLiveMutations(
            [Values("J", "W")] string format, [Values(false, true)] bool streamingReader)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(ReceivedSource, options, streamingReader);
            var additionalData = GetComposedAdditionalData(shark);
            Assert.That(additionalData.Keys, format == "W" ? Is.Empty : Is.EquivalentTo(new[] { "tracker", "backupTracker", "tag", "future" }));
            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.False, "Received source must not become an explicit override.");
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo("[]"));
            Assert.That(shark.Tag, Is.EqualTo("received"));
            var child = shark.Tracker;
            child.Bar = "mutated";

            string first = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            string second = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(first);
            var root = document.RootElement;
            Assert.That(shark.Tracker, Is.SameAs(child));
            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("mutated"));
            Assert.That(root.GetProperty("tracker").GetProperty("unknown").GetString(), Is.EqualTo("kept"));
            Assert.That(root.GetProperty("backupTracker").GetProperty("bar").GetString(), Is.EqualTo("untouched"));
            Assert.That(root.GetProperty("backupTracker").GetProperty("future").GetInt32(), Is.EqualTo(7));
            Assert.That(root.GetProperty("future").GetProperty("nested").GetBoolean(), Is.True);
            if (format != "W")
            {
                Assert.That(additionalData["future"].ToString(), Is.EqualTo("""{"nested":true}"""));
            }
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(root, "backupTracker"), Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo("[]"), "Serialization must not synthesize overrides.");
        }

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_AssignmentWithoutGetterAndNullAreIndependent(
            [Values("J", "W")] string format, [Values(false, true)] bool streamingReader)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(ReceivedSource, options, streamingReader);
            var replacement = new TrackerReference("replacement");
            shark.Tracker = replacement;
            shark.BackupTracker = null;
            replacement.Bar = "changed";

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            Assert.That(shark.Tracker, Is.SameAs(replacement));
            Assert.That(shark.BackupTracker, Is.Null);
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("changed"));
            Assert.That(document.RootElement.GetProperty("tracker").TryGetProperty("unknown", out _), Is.False);
            Assert.That(document.RootElement.TryGetProperty("backupTracker", out _), Is.False);
            Assert.That(document.RootElement.GetProperty("future").GetProperty("nested").GetBoolean(), Is.True);
        }

        [Test]
        [Category("ComposesSardFormat")]
        public void SardFormat_MixedReadersGateAdditionalDataWithoutChangingDeclaredRoutes(
            [Values(false, true)] bool nullable, [Values(false, true)] bool streaming, [Values("J", "W")] string format)
        {
            var options = new ModelReaderWriterOptions(format);
            object target = ReadMixedTarget(nullable, streaming, options);
            var additionalData = GetComposedAdditionalData(target);
            string otherUnknown = nullable ? "modelValueNotes" : "fooNotes";
            Assert.That(additionalData.Keys, format == "W" ? Is.Empty : Is.EquivalentTo(new[] { "tracker", "future", otherUnknown }));
            Assert.That(MixedPatch(target).TryGetJson("$.future"u8, out var source), Is.True,
                "The format gate must not change the existing full JSON patch seed.");
            Assert.That(Encoding.UTF8.GetString(source.Span), Is.EqualTo("""{"keep":1}"""));
            Assert.That(MixedPatch(target).Contains("$.future"u8), Is.False);
            var child = nullable ? ((NullableDynamicModel)target).ModelValue : ((DynamicModel)target).Foo;
            Assert.That(child.Bar, Is.EqualTo("declared"));
            ReadOnlySpan<byte> declaredPath = nullable ? "$.modelValue.extra"u8 : "$.foo.extra"u8;
            MixedPatch(target).Set(declaredPath, "declared raw");
            Assert.That(child.Patch.GetString("$.extra"u8), Is.EqualTo("declared raw"));
            Assert.That(MixedPatch(target).GetString(declaredPath), Is.EqualTo("declared raw"));
            GetMixedTracker(target).Bar = "composed live";

            using var document = JsonDocument.Parse(WriteMixedTarget(target, options));
            var root = document.RootElement;
            Assert.That(root.GetProperty(nullable ? "modelValue" : "foo").GetProperty("extra").GetString(), Is.EqualTo("declared raw"));
            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("composed live"));
            Assert.That(root.GetProperty("future").GetProperty("keep").GetInt32(), Is.EqualTo(1));
            if (format != "W")
            {
                Assert.That(additionalData["future"].ToString(), Is.EqualTo("""{"keep":1}"""));
            }
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesSardFormat")]
        public void SardFormat_DuplicateUnknownNamesUseDictionaryAddOnlyOutsideWireFormat(bool streamingReader)
        {
            const string json = """{"name":"mako","finCount":4,"future":1,"future":2}""";
            Assert.Throws<ArgumentException>(() => ReadSourceShark(json, ModelReaderWriterOptions.Json, streamingReader),
                "Dictionary.Add must reject duplicate unknown names instead of overwriting prior values.");
            var wire = ReadSourceShark(json, new ModelReaderWriterOptions("W"), streamingReader);
            Assert.That(GetComposedAdditionalData(wire), Is.Empty);
            Assert.That(wire.Name, Is.EqualTo("mako"));
            Assert.That(wire.FinCount, Is.EqualTo(4));
        }

        private static IDictionary<string, BinaryData> GetComposedAdditionalData(object target)
        {
            Type declaringType = target is MyShark ? typeof(MyShark)
                : target is DynamicModel ? typeof(DynamicModel) : typeof(NullableDynamicModel);
            return (IDictionary<string, BinaryData>)declaringType
                .GetField("_additionalBinaryDataProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        }

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_ReadingReceivedNullDoesNotAssignNull(
            [Values("J", "W")] string format, [Values(false, true)] bool streamingReader)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark("""{"name":"mako","finCount":4,"tracker":null}""", options, streamingReader);
            Assert.That(shark.Tracker, Is.Null);
            using var readOnly = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            Assert.That(readOnly.RootElement.GetProperty("tracker").ValueKind, Is.EqualTo(JsonValueKind.Null));

            shark.Tracker = null;
            using var assignedNull = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            Assert.That(assignedNull.RootElement.TryGetProperty("tracker", out _), Is.False);
        }

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_SourceEqualExplicitSetRemainsAuthoritative(
            [Values("J", "W")] string format, [Values(false, true)] bool rawBeforeGetter)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(ReceivedSource, options, streamingReader: false);
            if (!rawBeforeGetter)
            {
                _ = shark.Tracker;
            }
            shark.Patch.Set("$.tracker"u8, """{"bar":"service","unknown":"kept"}"""u8);
            shark.Tracker.Bar = "mutated";

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            Assert.That(shark.Patch.Contains("$.tracker"u8), Is.True, "An explicit edit equal to source is still an override.");
            Assert.That(shark.Tracker.Bar, Is.EqualTo("mutated"));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("service"));
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
        }

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_ReceivedRootAndAnchorOverridesRemainAuthoritative(
            [Values("J", "W")] string format, [Values(false, true)] bool rawBeforeGetter,
            [Values("set", "remove", "null", "root")] string edit)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(ReceivedSource, options, streamingReader: false);
            if (!rawBeforeGetter)
            {
                _ = shark.Tracker;
            }
            switch (edit)
            {
                case "set":
                    shark.Patch.Set("$.tracker"u8, """{"bar":"raw"}"""u8);
                    break;
                case "remove":
                    shark.Patch.Remove("$.tracker"u8);
                    break;
                case "null":
                    shark.Patch.SetNull("$.tracker"u8);
                    break;
                case "root":
                    shark.Patch.Set("$"u8, """{"tracker":{"bar":"raw"},"root":true}"""u8);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(edit));
            }
            if (rawBeforeGetter)
            {
                _ = shark.Tracker;
            }
            shark.Tracker = new TrackerReference("later live");
            string before = shark.Patch.ToBinaryData().ToString();
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            var root = document.RootElement;
            Assert.That(shark.Tracker.Bar, Is.EqualTo("later live"));
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(edit == "remove" ? 0 : 1));
            switch (edit)
            {
                case "set":
                case "root":
                    Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("raw"));
                    break;
                case "null":
                    Assert.That(root.GetProperty("tracker").ValueKind, Is.EqualTo(JsonValueKind.Null));
                    break;
            }
            if (edit == "root")
            {
                Assert.That(root.EnumerateObject().Count(), Is.EqualTo(2));
                Assert.That(root.GetProperty("root").GetBoolean(), Is.True);
            }
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(before));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_ReceivedOrdinaryGraphSendsListAndDictionaryMutations(string format)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(
                "{\"name\":\"mako\",\"finCount\":4,\"profile\":" + ReadProfileJson() + "}", options, streamingReader: true);
            var profile = shark.Profile;
            profile.RequiredModel.RequiredNullableList.Add(4);
            profile.RequiredRecordUnknown["added"] = BinaryData.FromString("\"live\"");
            profile.RequiredModel.OptionalNullableString = "nested live";

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            var written = document.RootElement.GetProperty("profile");
            Assert.That(shark.Profile, Is.SameAs(profile));
            Assert.That(written.GetProperty("requiredModel").GetProperty("requiredNullableList").EnumerateArray().Select(x => x.GetInt32()),
                Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(written.GetProperty("requiredRecordUnknown").GetProperty("added").GetString(), Is.EqualTo("live"));
            Assert.That(written.GetProperty("requiredModel").GetProperty("optionalNullableString").GetString(), Is.EqualTo("nested live"));
            Assert.That(written.GetProperty("extra").GetString(), Is.EqualTo("stuff"));
        }

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_RegistrationAllowsCollisionsAndCopiesNames()
        {
            var shark = ReadSourceShark(
                """{"name":"mako","finCount":4,"registeredOwnershipProbe":"source","mutatedNameProbe":"untouched"}""",
                ModelReaderWriterOptions.Json, streamingReader: false);
            Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> first = (writer, target, _) =>
            {
                if (ReferenceEquals(target, shark))
                    writer.WriteString("registeredOwnershipProbe", "first");
            };
            Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> second = (writer, target, _) =>
            {
                if (ReferenceEquals(target, shark))
                    writer.WriteString("registeredOwnershipProbe", "second");
            };
            string[] names = { "registeredOwnershipProbe", "registeredOwnershipProbe" };
            MyShark.AddComposedWriter(first, names);
            names[0] = names[1] = "mutatedNameProbe";
            Assert.DoesNotThrow(() => MyShark.AddComposedWriter(second, "registeredOwnershipProbe"));
            Assert.DoesNotThrow(() => MyShark.AddComposedWriter(first, "registeredOwnershipProbe"));
            Assert.DoesNotThrow(() => MyShark.AddComposedWriter(second, "name", "finCount"));
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(document.RootElement.EnumerateObject().Where(property => property.NameEquals("registeredOwnershipProbe"))
                .Select(property => property.Value.GetString()), Is.EqualTo(new[] { "first", "second" }),
                "Both callbacks remain registered once; the client author must prevent duplicate property output.");
            Assert.That(document.RootElement.GetProperty("mutatedNameProbe").GetString(), Is.EqualTo("untouched"),
                "Registration copies the names rather than retaining the caller's mutable array.");
            Assert.Throws<ArgumentNullException>(() => MyShark.AddComposedWriter(null!));
            Assert.Throws<ArgumentNullException>(() => MyShark.AddComposedWriter(second, (string[])null!));
            Assert.Throws<ArgumentNullException>(() => MyShark.AddComposedWriter(second, new string[] { null! }));
        }

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_OneWriteUsesOneRegistrationSnapshot()
        {
            const string propertyName = "sourceSnapshotProbe";
            bool registered = false;
            Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> second = (writer, target, _) =>
            {
                if (target.Name == "snapshot-probe" && !target.Patch.Contains("$.sourceSnapshotProbe"u8))
                {
                    writer.WriteString(propertyName, "composed");
                }
            };
            MyShark.AddComposedWriter((_, target, _) =>
            {
                if (target.Name == "snapshot-probe" && !registered)
                {
                    MyShark.AddComposedWriter(second, propertyName);
                    registered = true;
                }
            });
            var shark = ReadSourceShark(
                """{"name":"snapshot-probe","finCount":4,"sourceSnapshotProbe":"received"}""",
                ModelReaderWriterOptions.Json, streamingReader: false);

            using var first = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            using var next = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));

            Assert.That(first.RootElement.GetProperty(propertyName).GetString(), Is.EqualTo("received"),
                "A newly registered name must not hide source before its callback joins the snapshot.");
            Assert.That(next.RootElement.GetProperty(propertyName).GetString(), Is.EqualTo("composed"));
            Assert.That(GetRootPropertyCount(first.RootElement, propertyName), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(next.RootElement, propertyName), Is.EqualTo(1));
        }

        [Test]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_UnclaimedUnknownNamesRespectWholeValueOverrides(
            [Values("J", "W")] string format, [Values(false, true)] bool remove)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(
                """{"name":"mako","finCount":4,"unknown.with.dot":{"source":true},"another":7}""", options, streamingReader: true);
            if (remove)
            {
                shark.Patch.Remove("$['unknown.with.dot']"u8);
            }
            else
            {
                shark.Patch.Set("$['unknown.with.dot']"u8, """{"raw":true}"""u8);
            }

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, "unknown.with.dot"), Is.EqualTo(remove ? 0 : 1));
            if (!remove)
            {
                Assert.That(document.RootElement.GetProperty("unknown.with.dot").GetProperty("raw").GetBoolean(), Is.True);
                Assert.That(document.RootElement.GetProperty("unknown.with.dot").TryGetProperty("source", out _), Is.False);
            }
            Assert.That(document.RootElement.GetProperty("another").GetInt32(), Is.EqualTo(7));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_RegistrationTimingDoesNotAffectCapture(bool registerBeforeRead)
        {
            string name = registerBeforeRead ? "sourceRegisteredBefore" : "sourceRegisteredAfter";
            byte[] path = Encoding.UTF8.GetBytes("$." + name);
            var options = ModelReaderWriterOptions.Json;
            Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> callback = (writer, target, _) =>
            {
                if (!target.Patch.Contains(path) && target.Patch.TryGetJson(path, out var received))
                {
                    writer.WritePropertyName(name);
                    writer.WriteRawValue(received.Span);
                }
            };
            if (registerBeforeRead)
            {
                RegisterSourceWriter(callback, name);
            }
            var shark = ReadSourceShark(
                "{\"name\":\"mako\",\"finCount\":4,\"" + name + "\":{\"bar\":\"source\"}}", options, streamingReader: true);
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo("[]"));
            if (!registerBeforeRead)
            {
                using var unregistered = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
                Assert.That(unregistered.RootElement.GetProperty(name).GetProperty("bar").GetString(), Is.EqualTo("source"));
                RegisterSourceWriter(callback, name);
            }

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, name), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty(name).GetProperty("bar").GetString(), Is.EqualTo("source"));
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo("[]"));
        }

        [TestCase("$.tracker.extra", "\"raw\"")]
        [TestCase("$.tracker.unknown", "\"changed\"")]
        [Category("ComposesSourceRequirement")]
        [Category("ComposesPerLeafAlternative")]
        public void Shark_SourceRequirement_ReceivedDescendantEditStaysInsideItsMember(string path, string raw)
        {
            var shark = ReadSourceShark(ReceivedSource, ModelReaderWriterOptions.Json, streamingReader: false);
            shark.Tracker.Bar = "mutated";
            shark.Patch.Set(Encoding.UTF8.GetBytes(path), BinaryData.FromString(raw));

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            var root = document.RootElement;
            var tracker = root.GetProperty("tracker");
            string propertyName = path.Substring(path.LastIndexOf('.') + 1);
            Assert.Multiple(() =>
            {
                Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
                Assert.That(root.TryGetProperty(propertyName, out _), Is.False, "A descendant override must not escape to the root.");
                Assert.That(tracker.TryGetProperty(propertyName, out var value), Is.True);
                if (tracker.TryGetProperty(propertyName, out value))
                {
                    Assert.That(value.GetRawText(), Is.EqualTo(raw), "Explicit raw edits override the original child field.");
                }
                Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("mutated"),
                    "An unrelated modeled sibling still comes from the retained child.");
            });
        }

        [Test]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_ReceivedDescendantOverridesStayNested(
            [Values("J", "W")] string format, [Values(false, true)] bool rawBeforeGetter,
            [Values("set", "equal", "deep", "remove", "null", "append")] string edit)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(
                """{"name":"mako","finCount":4,"tracker":{"bar":"service","nested":{"count":1},"items":[1,2]}}""",
                options, streamingReader: false);
            if (!rawBeforeGetter)
            {
                _ = shark.Tracker;
            }
            switch (edit)
            {
                case "set":
                    shark.Patch.Set("$.tracker.bar"u8, "raw");
                    break;
                case "equal":
                    shark.Patch.Set("$.tracker.bar"u8, "service");
                    break;
                case "deep":
                    shark.Patch.Set("$.tracker.nested.count"u8, 2);
                    break;
                case "remove":
                    shark.Patch.Remove("$.tracker.bar"u8);
                    break;
                case "null":
                    shark.Patch.SetNull("$.tracker.bar"u8);
                    break;
                case "append":
                    shark.Patch.Append("$.tracker.items"u8, 3);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(edit));
            }
            if (rawBeforeGetter)
            {
                _ = shark.Tracker;
            }
            string first = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            string second = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            using var document = JsonDocument.Parse(first);
            var root = document.RootElement;
            var tracker = root.GetProperty("tracker");
            Assert.Multiple(() =>
            {
                Assert.That(root.EnumerateObject().Select(property => property.Name),
                    Is.EquivalentTo(new[] { "name", "finCount", "tracker" }), "Descendant operations must not escape to root.");
                Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
                Assert.That(second, Is.EqualTo(first));
                switch (edit)
                {
                    case "set":
                        Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("raw"));
                        break;
                    case "equal":
                        Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("service"));
                        Assert.That(shark.Patch.Contains("$.tracker"u8), Is.True, "An equal-source explicit edit still owns its member.");
                        break;
                    case "deep":
                        Assert.That(tracker.GetProperty("nested").GetProperty("count").GetInt32(), Is.EqualTo(2));
                        break;
                    case "remove":
                        Assert.That(tracker.TryGetProperty("bar", out _), Is.False);
                        break;
                    case "null":
                        Assert.That(tracker.GetProperty("bar").ValueKind, Is.EqualTo(JsonValueKind.Null));
                        break;
                    case "append":
                        Assert.That(tracker.GetProperty("items").EnumerateArray().Select(value => value.GetInt32()),
                            Is.EqualTo(new[] { 1, 2, 3 }));
                        break;
                }
            });
        }

        [TestCase("$.tracker.nested.count", "tracker")]
        [TestCase("$['tracker'].nested.count", "tracker")]
        [TestCase("$[\"tracker\"].nested.count", "tracker")]
        [TestCase("$['unknown.with.dot'].nested.count", "unknown.with.dot")]
        [TestCase("$[\"unknown.with.dot\"].nested.count", "unknown.with.dot")]
        [TestCase("$['single\\'name'].nested.count", "single'name")]
        [TestCase("$[\"quote\\\"name\"].nested.count", "quote\"name")]
        [TestCase("$['quote\"name'].nested.count", "quote\"name")]
        [TestCase("$[\"back\\\\slash\"].nested.count", "back\\slash")]
        [Category("ComposesSourcePathRequirement")]
        public void Shark_SourcePathRequirement_RootSelectorMatchesCapturedUnknownMetadata(string path, string name)
        {
            var shark = ReadSourceShark(
                "{\"name\":\"mako\",\"finCount\":4," + JsonSerializer.Serialize(name) + ":{\"nested\":{\"count\":1}}}",
                ModelReaderWriterOptions.Json, streamingReader: true);
            shark.Patch.Set(Encoding.UTF8.GetBytes(path), 2);

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, name), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty(name).GetProperty("nested").GetProperty("count").GetInt32(), Is.EqualTo(2));
            Assert.That(document.RootElement.EnumerateObject().Count(), Is.EqualTo(3));
        }

        [TestCase("single'name", "$[\"single'name\"]")]
        [TestCase("single'name", "$['single\\'name']")]
        [TestCase("quote\"name", "$['quote\"name']")]
        [TestCase("quote\"name", "$[\"quote\\\"name\"]")]
        [TestCase("back\\slash", "$[\"back\\\\slash\"]")]
        [TestCase("unknown.with.dot", "$['unknown.with.dot']")]
        [TestCase("bracket[name]", "$['bracket[name]']")]
        [TestCase("close]name", "$[\"close]name\"]")]
        [TestCase("\u00e9\u96ea", "$['\u00e9\u96ea']")]
        [TestCase("\u00e9\u96ea", "$[\"\\u00e9\\u96ea\"]")]
        [TestCase("literal\\u0041", "$[\"literal\\\\u0041\"]")]
        [Category("ComposesEscapedSourceRequirement")]
        public void Shark_EscapedSourceRequirement_SelectorsReadTheExactCapturedName(string name, string selector)
        {
            var shark = ReadSourceShark(
                "{\"name\":\"mako\",\"finCount\":4," + JsonSerializer.Serialize(name) + ":{\"bar\":\"source\",\"nested\":{\"count\":1},\"items\":[1,2]}}",
                ModelReaderWriterOptions.Json, streamingReader: true);
            using var untouched = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(untouched.RootElement, name), Is.EqualTo(1));
            Assert.That(untouched.RootElement.GetProperty(name).GetProperty("bar").GetString(), Is.EqualTo("source"));
            Assert.That(shark.Patch.TryGetJson(Encoding.UTF8.GetBytes(selector), out var json), Is.True,
                "Native source lookup must address the same literal name as captured source.");
            var child = ModelReaderWriter.Read<TrackerReference>(
                BinaryData.FromBytes(json), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            Assert.That(child.Bar, Is.EqualTo("source"));
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo("[]"));
        }

        [TestCase("single'name", "$[\"single'name\"]")]
        [TestCase("quote\"name", "$['quote\"name']")]
        [TestCase("back\\slash", "$[\"back\\\\slash\"]")]
        [TestCase("bracket[name]", "$['bracket[name]']")]
        [TestCase("\u00e9\u96ea", "$['\u00e9\u96ea']")]
        [TestCase("literal\\u0041", "$[\"literal\\\\u0041\"]")]
        [Category("ComposesEscapedSourceRequirement")]
        public void Shark_EscapedSourceRequirement_RawOperationsPreserveLiteralRootNames(string name, string selector)
        {
            var shark = ReadSourceShark(
                "{\"name\":\"mako\",\"finCount\":4," + JsonSerializer.Serialize(name) + ":{\"bar\":\"source\",\"nested\":{\"count\":1},\"items\":[1,2]}}",
                ModelReaderWriterOptions.Json, streamingReader: true);
            shark.Patch.Set(Encoding.UTF8.GetBytes(selector + ".nested.count"), 2);
            shark.Patch.Remove(Encoding.UTF8.GetBytes(selector + ".bar"));
            shark.Patch.SetNull(Encoding.UTF8.GetBytes(selector + ".newNull"));
            shark.Patch.Append(Encoding.UTF8.GetBytes(selector + ".items"), 3);

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, name), Is.EqualTo(1));
            Assert.That(document.RootElement.EnumerateObject().Count(), Is.EqualTo(3));
            var member = document.RootElement.GetProperty(name);
            Assert.That(member.GetProperty("nested").GetProperty("count").GetInt32(), Is.EqualTo(2));
            Assert.That(member.TryGetProperty("bar", out _), Is.False);
            Assert.That(member.GetProperty("newNull").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(member.GetProperty("items").EnumerateArray().Select(value => value.GetInt32()), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [TestCase("$['tracker")]
        [TestCase("$[\"tracker\"")]
        [TestCase("$['bad\\x'].bar")]
        [Category("ComposesSourceOwnership")]
        public void Shark_SourceOwnership_InvalidRootSelectorFailsBeforeChangingSource(string path)
        {
            var shark = ReadSourceShark(ReceivedSource, ModelReaderWriterOptions.Json, streamingReader: false);
            string before = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();
#if COMPOSES_JSONPATH_BUGFIX
            Assert.Throws<FormatException>(() => shark.Patch.Set(Encoding.UTF8.GetBytes(path), 1));
#else
            Assert.That(() => shark.Patch.Set(Encoding.UTF8.GetBytes(path), 1),
                Throws.InstanceOf<ArgumentException>().Or.InstanceOf<JsonException>());
#endif
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo("[]"));
            Assert.That(ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString(),
                Is.EqualTo(before));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesPrecedenceCharacterization")]
        public void Shark_RawPrecedence_ComposedOwnershipIsBroaderThanDeclaredDynamicChildPropagation(string format)
        {
            var options = new ModelReaderWriterOptions(format);
            var composed = ReadSourceShark("""{"name":"mako","finCount":4,"tracker":{"bar":"received"}}""",
                options, streamingReader: true);
            composed.Tracker.Bar = "live before";
            var dynamicParent = ModelReaderWriter.Read<NullableDynamicModel>(
                BinaryData.FromString("""{"modelValue":{"bar":"received"}}"""), options, SampleTypeSpecContext.Default)!;
            dynamicParent.ModelValue = new AnotherDynamicModel("live before");

            composed.Patch.Set("$.tracker.extra"u8, "raw");
            dynamicParent.Patch.Set("$.modelValue.extra"u8, "raw");
            using var composedBefore = JsonDocument.Parse(ModelReaderWriter.Write(composed, options, SampleTypeSpecContext.Default));
            using var dynamicOutput = JsonDocument.Parse(ModelReaderWriter.Write(dynamicParent, options, SampleTypeSpecContext.Default));
            Assert.That(composedBefore.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("received"));
            Assert.That(composedBefore.RootElement.GetProperty("tracker").GetProperty("extra").GetString(), Is.EqualTo("raw"));
            Assert.That(dynamicOutput.RootElement.GetProperty("modelValue").GetProperty("bar").GetString(), Is.EqualTo("live before"));
            Assert.That(dynamicOutput.RootElement.GetProperty("modelValue").GetProperty("extra").GetString(), Is.EqualTo("raw"));
            Assert.That(dynamicParent.Patch.Contains("$.modelValue"u8), Is.False,
                "The declared dynamic child receives a leaf edit; the parent does not take ownership of its whole member.");
            Assert.That(dynamicParent.ModelValue.Patch.Contains("$.extra"u8), Is.True);
            Assert.That(composed.Patch.Contains("$.tracker"u8), Is.True);

            composed.Tracker.Bar = "live after";
            using var composedAfter = JsonDocument.Parse(ModelReaderWriter.Write(composed, options, SampleTypeSpecContext.Default));
            Assert.That(composed.Tracker.Bar, Is.EqualTo("live after"), "The retained CLR reference is not rewritten by the raw edit.");
            Assert.That(composedAfter.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("received"),
                "Both earlier and later CLR values under an explicitly owned composed member are suppressed.");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesPrecedenceCharacterization")]
        public void Shark_RawPrecedence_CallerCanExplicitlySnapshotTypedStateBeforeRawEditing(string format)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark(ReceivedSource, options, streamingReader: true);
            shark.Tracker.Bar = "typed";
            shark.Patch.Set("$.tracker"u8, ModelReaderWriter.Write(
                shark.Tracker, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            shark.Patch.Set("$.tracker.extra"u8, "raw");
            shark.Tracker.Bar = "later CLR value";

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            var tracker = document.RootElement.GetProperty("tracker");
            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("typed"));
            Assert.That(tracker.GetProperty("unknown").GetString(), Is.EqualTo("kept"));
            Assert.That(tracker.GetProperty("extra").GetString(), Is.EqualTo("raw"));
            Assert.That(shark.Tracker.Bar, Is.EqualTo("later CLR value"),
                "Explicitly snapshotting typed state is a caller choice, not automatic live-model merging.");
        }

        [Test]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_LaterEditsUseTheCurrentOverrideNotTheReceivedSource()
        {
            var shark = ReadSourceShark(ReceivedSource, ModelReaderWriterOptions.Json, streamingReader: false);
            shark.Patch.Set("$.tracker"u8, """{"bar":"replacement","nested":{"count":5},"items":[1,2],"rawOnly":true}"""u8);
            shark.Patch.Set("$.tracker.nested.count"u8, 2);
            shark.Patch.Append("$.tracker.items"u8, 3);
            shark.Patch.Set("$.tracker.extra"u8, "raw");
            shark.Tracker = new TrackerReference("later live");
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            var tracker = document.RootElement.GetProperty("tracker");

            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("replacement"));
            Assert.That(tracker.GetProperty("nested").GetProperty("count").GetInt32(), Is.EqualTo(2));
            Assert.That(tracker.GetProperty("items").EnumerateArray().Select(value => value.GetInt32()), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(tracker.GetProperty("extra").GetString(), Is.EqualTo("raw"));
            Assert.That(tracker.GetProperty("rawOnly").GetBoolean(), Is.True);
            Assert.That(tracker.TryGetProperty("unknown", out _), Is.False, "Original received fields must not be resurrected.");
        }

        [Test]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_FailedEditDoesNotPublishAPartialMember()
        {
            var shark = ReadSourceShark(ReceivedSource, ModelReaderWriterOptions.Json, streamingReader: false);
            shark.Patch.Set("$.tracker.extra"u8, "prior raw");
            string before = shark.Patch.ToBinaryData().ToString();
            string beforePayload = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

            Assert.Catch<JsonException>(() => shark.Patch.Set("$.tracker.nested"u8, "{"u8));
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(before));
            Assert.That(ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString(),
                Is.EqualTo(beforePayload));
        }

        [Test]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_DescendantWriteDoesNotResurrectARemovedAnchor()
        {
            var shark = ReadSourceShark(ReceivedSource, ModelReaderWriterOptions.Json, streamingReader: false);
            shark.Patch.Remove("$.tracker"u8);
            shark.Patch.Set("$.tracker.bar"u8, "raw");

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(document.RootElement.TryGetProperty("tracker", out _), Is.False);
            Assert.That(document.RootElement.TryGetProperty("bar", out _), Is.False);
            Assert.That(shark.Patch.IsRemoved("$.tracker"u8), Is.True);
        }

        [Test]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_UnclaimedRootArraySupportsAppend()
        {
            var shark = ReadSourceShark("""{"name":"mako","finCount":4,"items":[1,2]}""",
                ModelReaderWriterOptions.Json, streamingReader: true);
            shark.Patch.Append("$.items"u8, 3);

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, "items"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("items").EnumerateArray().Select(value => value.GetInt32()),
                Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_RootArrayRepeatedAppendsUseCurrentOverride(string format)
        {
            var options = new ModelReaderWriterOptions(format);
            var shark = ReadSourceShark("""{"name":"mako","finCount":4,"items":[1,2]}""", options, streamingReader: true);
            shark.Patch.Append("$.items"u8, 3);
            shark.Patch.Append("$.items"u8, "three");
            shark.Patch.AppendNull("$.items"u8);

            using (var document = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default)))
            {
                Assert.That(GetRootPropertyCount(document.RootElement, "items"), Is.EqualTo(1));
                Assert.That(document.RootElement.GetProperty("items").GetRawText(), Is.EqualTo("""[1,2,3,"three",null]"""));
                Assert.That(shark.Patch.Contains("$.items"u8), Is.True);
            }

            shark.Patch.Set("$.items"u8, "[9]"u8);
            shark.Patch.Append("$.items"u8, 10);
            string first = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            string second = ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default).ToString();
            using var final = JsonDocument.Parse(first);
            Assert.That(GetRootPropertyCount(final.RootElement, "items"), Is.EqualTo(1));
            Assert.That(final.RootElement.GetProperty("items").GetRawText(), Is.EqualTo("[9,10]"));
            Assert.That(second, Is.EqualTo(first));
        }

        [TestCase("array", "[9]")]
        [TestCase("scalar", "\"raw\"")]
        [TestCase("null", "null")]
        [TestCase("remove", "")]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_RootArrayExactEditsKeepNativeSemantics(string edit, string expected)
        {
            var shark = ReadSourceShark("""{"name":"mako","finCount":4,"items":[1,2]}""",
                ModelReaderWriterOptions.Json, streamingReader: false);
            switch (edit)
            {
                case "null":
                    shark.Patch.SetNull("$.items"u8);
                    break;
                case "remove":
                    shark.Patch.Remove("$.items"u8);
                    break;
                default:
                    shark.Patch.Set("$.items"u8, BinaryData.FromString(expected));
                    break;
            }

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, "items"), Is.EqualTo(edit == "remove" ? 0 : 1));
            if (edit != "remove")
            {
                Assert.That(document.RootElement.GetProperty("items").GetRawText(), Is.EqualTo(expected));
            }
            else
            {
                shark.Patch.Set("$.items[0]"u8, 3);
                Assert.That(shark.Patch.IsRemoved("$.items"u8), Is.True);
                using var stillRemoved = JsonDocument.Parse(ModelReaderWriter.Write(
                    shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
                Assert.That(stillRemoved.RootElement.TryGetProperty("items", out _), Is.False);
            }
        }

        [TestCase("null")]
        [TestCase("\"not an array\"")]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_RootArrayInvalidAppendFailsBeforeChangingOverride(string raw)
        {
            var shark = ReadSourceShark("""{"name":"mako","finCount":4,"items":[1,2]}""",
                ModelReaderWriterOptions.Json, streamingReader: false);
            shark.Patch.Set("$.items"u8, BinaryData.FromString(raw));
            string before = shark.Patch.ToBinaryData().ToString();
            var native = new JsonPatch(Encoding.UTF8.GetBytes("""{"items":[1,2]}"""));
            native.Set("$.items"u8, BinaryData.FromString(raw));
            native.Append("$.items"u8, 3);
            Assert.Throws<ArgumentOutOfRangeException>(() => native.ToBinaryData("J"),
                "Stock SCM accepts the operation but cannot project an append onto a non-array.");
            var scoped = new JsonPatch(BinaryData.FromString(raw).ToMemory());
            scoped.Append("$"u8, 3);
            Assert.Catch<JsonException>(() => scoped.ToBinaryData("J"),
                "At the scoped root, stock SCM reports invalid JSON instead of the enclosing-path range error.");

            Assert.Catch<JsonException>(() => shark.Patch.Append("$.items"u8, 3),
                "Scoped projection surfaces its native error before publishing the member.");
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(before));
            using var actual = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(actual.RootElement, "items"), Is.EqualTo(1));
            Assert.That(actual.RootElement.GetProperty("items").GetRawText(), Is.EqualTo(raw));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesSourceRawRequirement")]
        public void Shark_SourceRawRequirement_ExplicitReAddAfterRemoveOrNullDoesNotRestoreSource(bool remove)
        {
            var shark = ReadSourceShark(ReceivedSource, ModelReaderWriterOptions.Json, streamingReader: false);
            var orphan = shark.Tracker;
            if (remove)
            {
                shark.Patch.Remove("$.tracker"u8);
            }
            else
            {
                shark.Patch.SetNull("$.tracker"u8);
            }
            shark.Tracker = new TrackerReference("later live");
            shark.Patch.Set("$.tracker"u8, """{"bar":"raw re-add","items":[1]}"""u8);
            shark.Patch.Append("$.tracker.items"u8, 2);
            orphan.Bar = "orphan mutation";

            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            var tracker = document.RootElement.GetProperty("tracker");
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(tracker.GetProperty("bar").GetString(), Is.EqualTo("raw re-add"));
            Assert.That(tracker.TryGetProperty("unknown", out _), Is.False);
            Assert.That(tracker.GetProperty("items").EnumerateArray().Select(value => value.GetInt32()), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(shark.Tracker.Bar, Is.EqualTo("later live"));
        }

        [TestCase("$['single\\'name'].nested.count", "single'name")]
        [TestCase("$[\"quote\\\"name\"].nested.count", "quote\"name")]
        [TestCase("$['quote\"name'].nested.count", "quote\"name")]
        [TestCase("$[\"back\\\\slash\"].nested.count", "back\\slash")]
        [Category("ComposesSourcePathCharacterization")]
        public void Shark_SourcePathCharacterization_EscapingMatchesTheSelectedRuntime(string path, string name)
        {
            var shark = ReadSourceShark(
                "{\"name\":\"mako\",\"finCount\":4," + JsonSerializer.Serialize(name) + ":{\"nested\":{\"count\":1}}}",
                ModelReaderWriterOptions.Json, streamingReader: true);
            string before = ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString();

#if COMPOSES_JSONPATH_BUGFIX
            shark.Patch.Set(Encoding.UTF8.GetBytes(path), 2);
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
            Assert.That(GetRootPropertyCount(document.RootElement, name), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty(name).GetProperty("nested").GetProperty("count").GetInt32(), Is.EqualTo(2));
#else
            Assert.Throws<NotSupportedException>(() => shark.Patch.Set(Encoding.UTF8.GetBytes(path), 2));
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo("[]"));
            Assert.That(ModelReaderWriter.Write(shark, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default).ToString(),
                Is.EqualTo(before));
#endif
        }

        private static MyShark ReadSourceShark(string json, ModelReaderWriterOptions options, bool streamingReader)
        {
            if (!streamingReader)
            {
                return ModelReaderWriter.Read<MyShark>(BinaryData.FromString(json), options, SampleTypeSpecContext.Default)!;
            }
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            return ((IJsonModel<MyShark>)new MyShark("reader", 0)).Create(ref reader, options)!;
        }

        private static void RegisterSourceWriter(
            Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> callback, params string[] names)
        {
            MyShark.AddComposedWriter(callback, names);
        }

#pragma warning restore SCME0001
    }
}
