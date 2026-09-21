// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using SampleTypeSpec;

namespace Microsoft.TypeSpec.Generator.ClientModel.Tests.ModelReaderWriterValidation.TestProjects.Sample_TypeSpec
{
    internal partial class ComposedModelsTests
    {
#pragma warning disable SCME0001

        [TestCase(typeof(DynamicModel))]
        [TestCase(typeof(NullableDynamicModel))]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_WriteHookCoexistsWithItsExistingPatch(Type target)
        {
            var hook = target.GetMethod("AddComposedWriter");
            Assert.That(hook, Is.Not.Null, "A second propagator pair cannot substitute for the target write hook.");
            Assert.That(hook!.GetParameters(), Has.Length.EqualTo(2));
            Assert.That(target.GetProperty("Patch"), Is.Not.Null);
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_ReceivedUnknownSourceMustNotShadowOrdinaryChildren()
        {
            var model = ModelReaderWriter.Read<NullableDynamicModel>(
                BinaryData.FromString("""{"modelValue":{"bar":"declared"},"tracker":{"bar":"source"}}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;

            Assert.That(model.Patch.Contains("$.tracker"u8), Is.False, "Received values must not become explicit whole-member overrides.");
            model.Patch.Set("$.modelValue.extra"u8, "leaf");
            Assert.That(model.ModelValue.Patch.GetString("$.extra"u8), Is.EqualTo("leaf"));
            Assert.That(model.Patch.GetString("$.modelValue.extra"u8), Is.EqualTo("leaf"),
                "The original generated getter must still resolve declared child state.");
        }

        private const string MixedDynamicJson = """
            {"name":"mixed","requiredNullableList":[],"requiredNullableDictionary":{},"primitiveDictionary":{},
             "foo":{"bar":"declared"},"listFoo":[{"bar":"list"}],"listOfListFoo":[[{"bar":"nested-list"}]],
             "dictionaryFoo":{"a":{"bar":"dictionary"}},"dictionaryOfDictionaryFoo":{"a":{"b":{"bar":"nested-dictionary"}}},
             "dictionaryListFoo":{"a":[{"bar":"dictionary-list"}]},"listOfDictionaryFoo":[{"a":{"bar":"list-dictionary"}}],
             "tracker":{"bar":"received","unknown":true},"future":{"keep":1},"fooNotes":{"keep":2}}
            """;

        private const string MixedNullableJson = """
            {"modelValue":{"bar":"declared"},"children":[{"bar":"list"}],"nestedChildren":[[{"bar":"nested-list"}]],
             "childDictionary":{"a":{"bar":"dictionary"}},"nestedChildDictionary":{"a":{"b":{"bar":"nested-dictionary"}}},
             "dictionaryChildren":{"a":[{"bar":"dictionary-list"}]},"listOfDictionaries":[{"a":{"bar":"list-dictionary"}}],
             "tracker":{"bar":"received","unknown":true},"future":{"keep":1},"modelValueNotes":{"keep":2}}
            """;

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_DeclaredRoutesAndOrdinaryCompositionCoexist(
            [Values(false, true)] bool nullable, [Values(false, true)] bool streaming, [Values("J", "W")] string format)
        {
            var options = new ModelReaderWriterOptions(format);
            object target = ReadMixedTarget(nullable, streaming, options);
            var tracker = GetMixedTracker(target);
            tracker.Bar = "live composed";
            string[] paths = nullable
                ? ["$.modelValue", "$.children[0]", "$.nestedChildren[0][0]", "$.childDictionary.a",
                    "$.nestedChildDictionary.a.b", "$.dictionaryChildren.a[0]", "$.listOfDictionaries[0].a"]
                : ["$.foo", "$.listFoo[0]", "$.listOfListFoo[0][0]", "$.dictionaryFoo.a",
                    "$.dictionaryOfDictionaryFoo.a.b", "$.dictionaryListFoo.a[0]", "$.listOfDictionaryFoo[0].a"];
            foreach (string path in paths)
            {
                MixedPatch(target).Set(Encoding.UTF8.GetBytes(path + ".extra"), "raw child");
                Assert.That(MixedPatch(target).GetString(Encoding.UTF8.GetBytes(path + ".extra")), Is.EqualTo("raw child"));
            }
            var declared = nullable ? ((NullableDynamicModel)target).ModelValue : ((DynamicModel)target).Foo;
            Assert.That(declared.Patch.GetString("$.extra"u8), Is.EqualTo("raw child"));
            Assert.That(MixedPatch(target).Contains(Encoding.UTF8.GetBytes(paths[0])), Is.False);
            using var document = JsonDocument.Parse(WriteMixedTarget(target, options));
            var root = document.RootElement;
            Assert.That(root.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("live composed"));
            Assert.That(root.GetProperty("tracker").GetProperty("unknown").GetBoolean(), Is.True);
            Assert.That(GetMixedTracker(target), Is.SameAs(tracker));
            Assert.That(root.GetProperty(nullable ? "modelValue" : "foo").GetProperty("bar").GetString(), Is.EqualTo("declared"));
            Assert.That(root.GetProperty(nullable ? "modelValue" : "foo").GetProperty("extra").GetString(), Is.EqualTo("raw child"));
            Assert.That(root.GetProperty("future").GetProperty("keep").GetInt32(), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));

            MixedPatch(target).Set("$.tracker.nested.value"u8, 7);
            tracker.Bar = "not sent";
            using var overridden = JsonDocument.Parse(WriteMixedTarget(target, options));
            Assert.That(overridden.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("received"));
            Assert.That(overridden.RootElement.GetProperty("tracker").GetProperty("nested").GetProperty("value").GetInt32(), Is.EqualTo(7));
            Assert.That(MixedPatch(target).GetString(Encoding.UTF8.GetBytes(paths[0] + ".extra")), Is.EqualTo("raw child"),
                "Composed raw ownership must not overwrite the declared getter/child route.");
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_UnknownPrefixMustNotEnterADeclaredChildRoute(bool nullable)
        {
            object target = ReadMixedTarget(nullable, false, ModelReaderWriterOptions.Json);
            string rootName = nullable ? "modelValueNotes" : "fooNotes";
            MixedPatch(target).Set(Encoding.UTF8.GetBytes("$." + rootName + ".extra"), "unknown raw");
            using var document = JsonDocument.Parse(WriteMixedTarget(target, ModelReaderWriterOptions.Json));
            Assert.That(document.RootElement.GetProperty(rootName).GetProperty("extra").GetString(), Is.EqualTo("unknown raw"));
            var declared = nullable ? ((NullableDynamicModel)target).ModelValue : ((DynamicModel)target).Foo;
            Assert.That(declared.Patch.TryGetJson("$.Notes.extra"u8, out _), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_QuotedDeclaredRootsConsumeTheWholeSelectorBeforeForwarding(bool nullable)
        {
            object target = ReadMixedTarget(nullable, true, ModelReaderWriterOptions.Json);
            string[] paths = nullable
                ? ["$['modelValue']['extra']", "$[\"children\"][0]['extra']", "$['childDictionary']['a']['extra']",
                    "$['nestedChildren'][0][0]['extra']", "$['nestedChildDictionary']['a']['b']['extra']",
                    "$['dictionaryChildren']['a'][0]['extra']", "$['listOfDictionaries'][0]['a']['extra']"]
                : ["$['foo']['extra']", "$[\"listFoo\"][0]['extra']", "$['dictionaryFoo']['a']['extra']",
                    "$['listOfListFoo'][0][0]['extra']", "$['dictionaryOfDictionaryFoo']['a']['b']['extra']",
                    "$['dictionaryListFoo']['a'][0]['extra']", "$['listOfDictionaryFoo'][0]['a']['extra']"];
            foreach (string path in paths)
            {
                MixedPatch(target).Set(Encoding.UTF8.GetBytes(path), "quoted route");
                Assert.That(MixedPatch(target).GetString(Encoding.UTF8.GetBytes(path)), Is.EqualTo("quoted route"));
            }
            GetMixedTracker(target).Bar = "composed";
            using var document = JsonDocument.Parse(WriteMixedTarget(target, ModelReaderWriterOptions.Json));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("composed"));
            Assert.That(document.RootElement.GetProperty(nullable ? "modelValue" : "foo").GetProperty("extra").GetString(),
                Is.EqualTo("quoted route"));
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_ConstructorsKeepDeclaredPropagators([Values(false, true)] bool nullable, [Values(false, true)] bool factory)
        {
            var child = new AnotherDynamicModel("constructed");
            object target = nullable
                ? factory ? SampleTypeSpecModelFactory.NullableDynamicModel(modelValue: child) : new NullableDynamicModel { ModelValue = child }
                : factory ? SampleTypeSpecModelFactory.DynamicModel(name: "constructed", foo: child)
                    : new DynamicModel("constructed", [], new Dictionary<string, int>(), new Dictionary<string, int>(), child, [], [],
                        new Dictionary<string, AnotherDynamicModel>(), new Dictionary<string, IDictionary<string, AnotherDynamicModel>>(),
                        new Dictionary<string, IList<AnotherDynamicModel>>(), []);
            var tracker = new TrackerReference("composed");
            SetMixedTracker(target, tracker);
            MixedPatch(target).Set(nullable ? "$.modelValue.extra"u8 : "$.foo.extra"u8, "forwarded");
            Assert.That(child.Patch.GetString("$.extra"u8), Is.EqualTo("forwarded"));
            Assert.That(MixedPatch(target).GetString(nullable ? "$.modelValue.extra"u8 : "$.foo.extra"u8), Is.EqualTo("forwarded"));
            tracker.Bar = "changed";
            using var document = JsonDocument.Parse(WriteMixedTarget(target, ModelReaderWriterOptions.Json));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("changed"));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_OrdinaryGraphAndReferenceSemanticsRemainIndependent(bool nullable)
        {
            object target = ReadMixedTarget(nullable, true, ModelReaderWriterOptions.Json);
            var profile = ModelReaderWriter.Read<RoundTripModel>(
                BinaryData.FromString(ReadProfileJson()), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            if (target is DynamicModel dynamicTarget)
                dynamicTarget.Profile = profile;
            else
                ((NullableDynamicModel)target).Profile = profile;
            var old = GetMixedTracker(target);
            var replacement = new TrackerReference("replacement");
            SetMixedTracker(target, replacement);
            old.Bar = "orphan";
            profile.RequiredModel.OptionalNullableString = "live nested";
            profile.RequiredModel.RequiredNullableList.Add(4);
            profile.RequiredRecordUnknown["new"] = BinaryData.FromString("\"live dictionary\"");

            using var document = JsonDocument.Parse(WriteMixedTarget(target, new ModelReaderWriterOptions("W")));
            Assert.That(GetMixedTracker(target), Is.SameAs(replacement));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("replacement"));
            var written = document.RootElement.GetProperty("profile");
            Assert.That(written.GetProperty("requiredModel").GetProperty("optionalNullableString").GetString(), Is.EqualTo("live nested"));
            Assert.That(written.GetProperty("requiredModel").GetProperty("requiredNullableList").GetArrayLength(), Is.EqualTo(4));
            Assert.That(written.GetProperty("requiredRecordUnknown").GetProperty("new").GetString(), Is.EqualTo("live dictionary"));
            Assert.That(written.GetProperty("extra").GetString(), Is.EqualTo("stuff"), "Retained composed values preserve their JSON representation.");
            SetMixedTracker(target, null!);
            using var cleared = JsonDocument.Parse(WriteMixedTarget(target, ModelReaderWriterOptions.Json));
            Assert.That(cleared.RootElement.TryGetProperty("tracker", out _), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_OneRegistrationPairOwnsBothDirections(bool nullable)
        {
            object target = ReadMixedTarget(nullable, true, ModelReaderWriterOptions.Json);
            object boxed = MixedPatch(target);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var setter = (Delegate)typeof(JsonPatch).GetField("_propagatorSetter", flags)!.GetValue(boxed)!;
            var getter = (Delegate)typeof(JsonPatch).GetField("_propagatorGetter", flags)!.GetValue(boxed)!;
            Assert.That(setter.GetInvocationList(), Has.Length.EqualTo(1));
            Assert.That(getter.GetInvocationList(), Has.Length.EqualTo(1));
            Assert.That(setter.Target, Is.SameAs(target));
            Assert.That(getter.Target, Is.SameAs(target), "The generated getter is retained, not replaced by the source-only no-op.");
            Assert.That(getter.Method.Name, Is.EqualTo("PropagateGet"));
            Assert.That(setter.Method.Name, Is.EqualTo("PropagateSet"));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_RootOverrideAndFailedRawEditPreserveDeclaredState(bool nullable)
        {
            object target = ReadMixedTarget(nullable, false, ModelReaderWriterOptions.Json);
            _ = GetMixedTracker(target);
            string before = WriteMixedTarget(target, ModelReaderWriterOptions.Json).ToString();
            Assert.Catch<JsonException>(() => MixedPatch(target).Set("$.tracker.extra"u8, "{"u8));
            Assert.That(WriteMixedTarget(target, ModelReaderWriterOptions.Json).ToString(), Is.EqualTo(before));
            MixedPatch(target).Set("$"u8, """{"root":"authoritative"}"""u8);
            Assert.That(WriteMixedTarget(target, ModelReaderWriterOptions.Json).ToString(), Is.EqualTo("""{"root":"authoritative"}"""));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_RegistrationAllowsDeclaredAndDuplicateNames(bool nullable)
        {
            object target = ReadMixedTarget(nullable, false, ModelReaderWriterOptions.Json);
            _ = GetMixedTracker(target);
            if (nullable)
            {
                Action<Utf8JsonWriter, NullableDynamicModel, ModelReaderWriterOptions> writer = (json, model, _) =>
                {
                    if (ReferenceEquals(model, target))
                        json.WriteString("modelValue", "first");
                };
                Action<Utf8JsonWriter, NullableDynamicModel, ModelReaderWriterOptions> second = (json, model, _) =>
                {
                    if (ReferenceEquals(model, target))
                        json.WriteString("modelValue", "second");
                };
                Assert.DoesNotThrow(() => NullableDynamicModel.AddComposedWriter(writer, "modelValue", "modelValue", "childDictionary", "tracker"));
                Assert.DoesNotThrow(() => NullableDynamicModel.AddComposedWriter(second, "modelValue", "tracker"));
                Parallel.For(0, 16, _ => NullableDynamicModel.AddComposedWriter(writer));
                Assert.Throws<ArgumentNullException>(() => NullableDynamicModel.AddComposedWriter(null!));
                Assert.Throws<ArgumentNullException>(() => NullableDynamicModel.AddComposedWriter(writer, (string[])null!));
                Assert.Throws<ArgumentNullException>(() => NullableDynamicModel.AddComposedWriter(writer, new string[] { null! }));
            }
            else
            {
                Action<Utf8JsonWriter, DynamicModel, ModelReaderWriterOptions> writer = (json, model, _) =>
                {
                    if (ReferenceEquals(model, target))
                        json.WriteString("foo", "first");
                };
                Action<Utf8JsonWriter, DynamicModel, ModelReaderWriterOptions> second = (json, model, _) =>
                {
                    if (ReferenceEquals(model, target))
                        json.WriteString("foo", "second");
                };
                Assert.DoesNotThrow(() => DynamicModel.AddComposedWriter(writer, "foo", "foo", "dictionaryFoo", "tracker"));
                Assert.DoesNotThrow(() => DynamicModel.AddComposedWriter(second, "foo", "tracker"));
                Parallel.For(0, 16, _ => DynamicModel.AddComposedWriter(writer));
                Assert.Throws<ArgumentNullException>(() => DynamicModel.AddComposedWriter(null!));
                Assert.Throws<ArgumentNullException>(() => DynamicModel.AddComposedWriter(writer, (string[])null!));
                Assert.Throws<ArgumentNullException>(() => DynamicModel.AddComposedWriter(writer, new string[] { null! }));
            }
            using var document = JsonDocument.Parse(WriteMixedTarget(target, ModelReaderWriterOptions.Json));
            var occurrences = document.RootElement.EnumerateObject()
                .Where(property => property.NameEquals(nullable ? "modelValue" : "foo")).ToArray();
            Assert.That(occurrences, Has.Length.EqualTo(3), "The declaration and both callbacks write without name reconciliation.");
            Assert.That(occurrences[0].Value.GetProperty("bar").GetString(), Is.EqualTo("declared"));
            Assert.That(occurrences.Skip(1).Select(property => property.Value.GetString()), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(GetRootPropertyCount(document.RootElement, "tracker"), Is.EqualTo(1));
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_InheritedPatchKeepsDerivedThenBaseThenComposedDispatch(
            [Values("public", "json", "persist")] string creation, [Values("J", "W")] string format)
        {
            var options = new ModelReaderWriterOptions(format);
            var target = CreateMixedDerived();
            if (creation != "public")
            {
                var data = BinaryData.FromString(
                    """{"modelValue":{"bar":"base"},"derivedChild":{"bar":"derived"},"tracker":{"bar":"received","future":true},"futureRoot":3}""");
                if (creation == "json")
                {
                    var reader = new Utf8JsonReader(data.ToMemory().Span);
                    target = ((IJsonModel<NullableDynamicModel>)target).Create(ref reader, options)!;
                }
                else
                {
                    target = ((IPersistableModel<NullableDynamicModel>)target).Create(data, options)!;
                }
            }
            var derived = (AnotherDynamicModel)target.GetType().GetProperty("DerivedChild")!.GetValue(target)!;
            Assert.That(typeof(NullableDynamicModel).GetProperty("Patch")!.DeclaringType, Is.EqualTo(typeof(NullableDynamicModel)));
            var tracker = creation == "public" ? new TrackerReference("typed") : target.Tracker;
            target.Tracker = tracker;
            tracker.Bar = "typed";
            target.Patch.Set("$.derivedChild.extra"u8, "derived raw");
            target.Patch.Set("$.modelValue.extra"u8, "base raw");
            Assert.That(derived.Patch.GetString("$.extra"u8), Is.EqualTo("derived raw"));
            Assert.That(target.ModelValue.Patch.GetString("$.extra"u8), Is.EqualTo("base raw"));
            Assert.That(target.Patch.GetString("$.derivedChild.extra"u8), Is.EqualTo("derived raw"));
            Assert.That(target.Patch.GetString("$.modelValue.extra"u8), Is.EqualTo("base raw"));
            using var document = JsonDocument.Parse(WriteMixedDerived(target, options));
            Assert.That(GetRootPropertyCount(document.RootElement, "derivedChild"), Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("derivedChild").GetProperty("extra").GetString(), Is.EqualTo("derived raw"));
            Assert.That(document.RootElement.GetProperty("modelValue").GetProperty("extra").GetString(), Is.EqualTo("base raw"));
            Assert.That(document.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("typed"));
            if (creation != "public")
            {
                var additionalData = GetComposedAdditionalData(target);
                Assert.That(additionalData.Keys, format == "W" ? Is.Empty : Is.EquivalentTo(new[] { "tracker", "futureRoot" }));
                Assert.That(document.RootElement.GetProperty("futureRoot").GetInt32(), Is.EqualTo(3));
            }
            Assert.That(target.Patch.Contains("$.derivedChild"u8), Is.False, "A derived declared child must not be captured/written twice.");

            target.Patch.Set("$.tracker"u8, """{"bar":"explicit"}"""u8);
            target.Tracker.Bar = "unsent";
            using var patched = JsonDocument.Parse(WriteMixedDerived(target, options));
            Assert.That(patched.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("explicit"));
            Assert.That(target.Patch.GetString("$.derivedChild.extra"u8), Is.EqualTo("derived raw"));
            Assert.That(target.Patch.GetString("$.modelValue.extra"u8), Is.EqualTo("base raw"));
            object boxed = target.Patch;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            foreach (string field in new[] { "_propagatorSetter", "_propagatorGetter" })
            {
                var callback = (Delegate)typeof(JsonPatch).GetField(field, flags)!.GetValue(boxed)!;
                Assert.That(callback.Target, Is.SameAs(target));
                Assert.That(callback.Method.DeclaringType, Is.EqualTo(target.GetType()), "The active pair is derived dispatch, which delegates to base.");
                Assert.That(callback.GetInvocationList(), Has.Length.EqualTo(1));
            }
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_InheritedDeclaredNameIsTheClientAuthorsResponsibility()
        {
            const string name = "derivedCollisionProbe";
            var target = CreateMixedDerived(name);
            NullableDynamicModel.AddComposedWriter((writer, model, _) =>
            {
                if (ReferenceEquals(model, target))
                    writer.WriteString(name, "composed");
            }, name);
            using var document = JsonDocument.Parse(WriteMixedDerived(target, ModelReaderWriterOptions.Json));
            var occurrences = document.RootElement.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
            Assert.That(occurrences, Has.Length.EqualTo(2), "Serialization must not reject or reconcile a client author's name collision.");
            Assert.That(occurrences[0].Value.GetProperty("bar").GetString(), Is.EqualTo("derived"));
            Assert.That(occurrences[1].Value.GetString(), Is.EqualTo("composed"));
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_RegistrationAfterReadingAndDuringWritingKeepsOneSnapshot()
        {
            var model = ModelReaderWriter.Read<NullableDynamicModel>(
                BinaryData.FromString("""{"modelValue":{"bar":"base"},"mixedSnapshot":"received"}"""),
                ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            using var before = JsonDocument.Parse(WriteMixedTarget(model, ModelReaderWriterOptions.Json));
            Assert.That(before.RootElement.GetProperty("mixedSnapshot").GetString(), Is.EqualTo("received"));
            bool registered = false;
            Action<Utf8JsonWriter, NullableDynamicModel, ModelReaderWriterOptions> writeName =
                static (writer, target, options) =>
                {
                    if (!target.Patch.Contains("$.mixedSnapshot"u8) && target.Patch.TryGetJson("$.mixedSnapshot"u8, out _))
                    {
                        writer.WriteString("mixedSnapshot", "callback");
                    }
                };
            NullableDynamicModel.AddComposedWriter((_, target, _) =>
            {
                if (ReferenceEquals(target, model) && !registered)
                {
                    NullableDynamicModel.AddComposedWriter(writeName, "mixedSnapshot");
                    registered = true;
                }
            });
            using var first = JsonDocument.Parse(WriteMixedTarget(model, ModelReaderWriterOptions.Json));
            using var second = JsonDocument.Parse(WriteMixedTarget(model, ModelReaderWriterOptions.Json));
            Assert.That(first.RootElement.GetProperty("mixedSnapshot").GetString(), Is.EqualTo("received"));
            Assert.That(second.RootElement.GetProperty("mixedSnapshot").GetString(), Is.EqualTo("callback"));
            Assert.That(GetRootPropertyCount(first.RootElement, "mixedSnapshot"), Is.EqualTo(1));
            Assert.That(GetRootPropertyCount(second.RootElement, "mixedSnapshot"), Is.EqualTo(1));
            model.Patch.Set("$.modelValue.extra"u8, "still routed");
            Assert.That(model.Patch.GetString("$.modelValue.extra"u8), Is.EqualTo("still routed"));
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_SharedOrdinaryChildIsNotMutatedByAnotherTargetsRawOverride()
        {
            var first = (DynamicModel)ReadMixedTarget(false, false, ModelReaderWriterOptions.Json);
            var second = (NullableDynamicModel)ReadMixedTarget(true, false, ModelReaderWriterOptions.Json);
            var shared = new TrackerReference("shared");
            first.Tracker = shared;
            second.Tracker = shared;
            first.Patch.Set("$.tracker"u8, """{"bar":"raw"}"""u8);
            shared.Bar = "live alias";
            using var firstJson = JsonDocument.Parse(WriteMixedTarget(first, ModelReaderWriterOptions.Json));
            using var secondJson = JsonDocument.Parse(WriteMixedTarget(second, ModelReaderWriterOptions.Json));
            Assert.That(first.Tracker, Is.SameAs(shared));
            Assert.That(second.Tracker, Is.SameAs(shared));
            Assert.That(firstJson.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("raw"));
            Assert.That(secondJson.RootElement.GetProperty("tracker").GetProperty("bar").GetString(), Is.EqualTo("live alias"));
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_SourceOnlyFreshConstructorsDoNotInstallNoOpPropagators()
        {
            var metadata = (MyShark)Activator.CreateInstance(typeof(MyShark), nonPublic: true)!;
            foreach (var model in new[] { new MyShark("fresh", 1), metadata })
            {
                object patch = model.Patch;
                foreach (string name in new[] { "_propagatorSetter", "_propagatorGetter" })
                {
                    Assert.That(typeof(JsonPatch).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(patch), Is.Null);
                }
            }
            var received = ReadSourceShark(ReceivedSource, ModelReaderWriterOptions.Json, streamingReader: true);
            object receivedPatch = received.Patch;
            var callback = (Delegate)typeof(JsonPatch).GetField("_propagatorSetter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receivedPatch)!;
            Assert.That(callback.Target, Is.SameAs(received), "The full constructor rebinds the incoming patch to its owner.");
        }

        [Test]
        [Category("ComposesMixedTarget")]
        public void MixedTarget_RuntimeProvenanceRetainsTheScm115Surface()
        {
            var assembly = typeof(JsonPatch).Assembly;
            TestContext.Out.WriteLine("SCM: " + assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
            TestContext.Out.WriteLine("Assembly: " + assembly.Location);
            Assert.That(assembly.GetName().Version, Is.EqualTo(new Version(1, 15, 0, 0)));
            using var dependencies = JsonDocument.Parse(File.ReadAllText(
                Path.ChangeExtension(typeof(ComposedModelsTests).Assembly.Location, ".deps.json")));
            string package = dependencies.RootElement.GetProperty("libraries").EnumerateObject()
                .Single(property => property.Name.StartsWith("System.ClientModel/", StringComparison.Ordinal)).Name;
            TestContext.Out.WriteLine("Package: " + package);
#if COMPOSES_JSONPATH_BUGFIX
            Assert.That(package, Does.StartWith("System.ClientModel/1.15.0-jsonpath.local."),
                "The escape guard may be lifted only for the opt-in bug-fix runtime.");
#else
            Assert.That(package, Is.EqualTo("System.ClientModel/1.15.0"));
#endif
            Assert.That(assembly.GetType("System.ClientModel.AsyncStreamingClientResult`1"), Is.Not.Null);
            Assert.That(assembly.GetType("System.ClientModel.Primitives.ModelMember"), Is.Null);
        }

        private static NullableDynamicModel CreateMixedDerived(string name = "derivedChild")
        {
            var type = typeof(NullableDynamicModel).Assembly.GetType("SampleTypeSpec.MixedDerivedDynamicModel")!;
            return (NullableDynamicModel)Activator.CreateInstance(type,
                new object[] { new AnotherDynamicModel("base"), new AnotherDynamicModel("derived"), name })!;
        }

        private static BinaryData WriteMixedDerived(NullableDynamicModel target, ModelReaderWriterOptions options)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                ((IJsonModel<NullableDynamicModel>)target).Write(writer, options);
            }
            return BinaryData.FromBytes(buffer.WrittenMemory);
        }

        private static object ReadMixedTarget(bool nullable, bool streaming, ModelReaderWriterOptions options)
        {
            var data = BinaryData.FromString(nullable ? MixedNullableJson : MixedDynamicJson);
            if (!streaming)
            {
                return nullable
                    ? ModelReaderWriter.Read<NullableDynamicModel>(data, options, SampleTypeSpecContext.Default)!
                    : ModelReaderWriter.Read<DynamicModel>(data, options, SampleTypeSpecContext.Default)!;
            }
            var reader = new Utf8JsonReader(data.ToMemory().Span);
            return nullable
                ? ((IJsonModel<NullableDynamicModel>)new NullableDynamicModel()).Create(ref reader, options)!
                : ((IJsonModel<DynamicModel>)Activator.CreateInstance(typeof(DynamicModel), nonPublic: true)!).Create(ref reader, options)!;
        }

        private static ref JsonPatch MixedPatch(object target)
        {
            if (target is DynamicModel dynamicTarget)
                return ref dynamicTarget.Patch;
            return ref ((NullableDynamicModel)target).Patch;
        }

        private static TrackerReference GetMixedTracker(object target)
            => target is DynamicModel dynamicTarget ? dynamicTarget.Tracker : ((NullableDynamicModel)target).Tracker;

        private static void SetMixedTracker(object target, TrackerReference value)
        {
            if (target is DynamicModel dynamicTarget)
                dynamicTarget.Tracker = value;
            else
                ((NullableDynamicModel)target).Tracker = value;
        }

        private static BinaryData WriteMixedTarget(object target, ModelReaderWriterOptions options)
            => target is DynamicModel dynamicTarget
                ? ModelReaderWriter.Write(dynamicTarget, options, SampleTypeSpecContext.Default)
                : ModelReaderWriter.Write((NullableDynamicModel)target, options, SampleTypeSpecContext.Default);

#pragma warning restore SCME0001
    }
}
