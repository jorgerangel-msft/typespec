// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_AssignmentRetainsLiveCollectionsAndDetachedAliases(string format)
        {
            var items = new List<string?> { "assigned", null };
            var first = new MyShark("first", 4) { Items = items };
            var second = new MyShark("second", 4) { Items = items };
            items[0] = "external";
            first.Items.Add("getter");
            first.Items.RemoveAt(1);
            Assert.That(first.Items, Is.SameAs(items));
            Assert.That(second.Items, Is.SameAs(items));
            AssertItemsOutput(first, format, """["external","getter"]""");
            AssertItemsOutput(second, format, """["external","getter"]""");
            Assert.That(first.Patch.Contains("$"u8, "items"u8), Is.False);

            var replacement = new List<string?> { "replacement" };
            first.Items = replacement;
            items.Add("orphan");
            Assert.That(first.Items, Is.SameAs(replacement));
            Assert.That(second.Items, Is.SameAs(items));
            AssertItemsOutput(first, format, """["replacement"]""");
            AssertItemsOutput(second, format, """["external","getter","orphan"]""");
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_FreshAssignmentFirstAppendOwnsTheWholeProperty(
            [Values("J", "W")] string format, [Values(false, true)] bool appendNull)
        {
            var items = new List<string?> { "assigned" };
            var shark = new MyShark("fresh", 4) { Items = items };
            items.Add("clr-only");
            AssertItemsOutput(shark, format, """["assigned","clr-only"]""");

            if (appendNull)
                shark.Patch.AppendNull("$.items"u8);
            else
                shark.Patch.Append("$.items"u8, "raw");
            Assert.That(shark.Patch.Contains("$.items"u8), Is.False);
            Assert.That(shark.Patch.Contains("$"u8, "items"u8), Is.True);
            AssertItemsOutput(shark, format, appendNull ? "[null]" : """["raw"]""");
            Assert.That(shark.Items, Is.SameAs(items));
            items.Add("still CLR-only");
            AssertItemsOutput(shark, format, appendNull ? "[null]" : """["raw"]""");

            shark.Patch.AppendNull("$.items"u8);
            AssertItemsOutput(shark, format, appendNull ? "[null,null]" : """["raw",null]""");
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_WReadMissingMemberDoesNotHideTheFirstAppend(
            [Values("J", "W")] string format, [Values(false, true)] bool appendNull)
        {
            var shark = ReadItemsShark(null);
            var items = new List<string?> { "assigned", "clr-only" };
            shark.Items = items;
            if (appendNull)
                shark.Patch.AppendNull("$.items"u8);
            else
                shark.Patch.Append("$.items"u8, "raw");
            Assert.That(shark.Patch.Contains("$.items"u8), Is.False,
                "The first raw append has no source root for the installed propagator to normalize.");
            var output = AssertItemsOutput(shark, format, appendNull ? "[null]" : """["raw"]""");
            Assert.That(output.GetProperty("name").GetString(), Is.EqualTo("declared-edited"));
            Assert.That(output.GetProperty("future").GetProperty("keep").GetBoolean(), Is.True);
            Assert.That(shark.Items, Is.SameAs(items));

            shark.Patch.AppendNull("$.items"u8);
            AssertItemsOutput(shark, format, appendNull ? "[null,null]" : """["raw",null]""");
            Assert.That(GetComposedAdditionalData(shark), Is.Empty);
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_MaterializedReferenceStaysLiveUntilRawTakeover(
            [Values("J", "W")] string format, [Values(false, true)] bool appendNull)
        {
            var shark = ReadItemsShark("""["received",null]""");
            var items = shark.Items;
            Assert.That(shark.Items, Is.SameAs(items));
            items[0] = "clr-changed";
            items.Add("clr-only");
            AssertItemsOutput(shark, format, """["clr-changed",null,"clr-only"]""");
            Assert.That(shark.Patch.Contains("$"u8, "items"u8), Is.False);

            if (appendNull)
                shark.Patch.AppendNull("$.items"u8);
            else
                shark.Patch.Append("$.items"u8, "raw");
            string expected = appendNull ? """["received",null,null]""" : """["received",null,"raw"]""";
            Assert.That(shark.Patch.Contains("$.items"u8), Is.True,
                "The received root is scoped into an explicit whole-member replacement.");
            AssertItemsOutput(shark, format, expected);
            items.Add("after takeover");
            Assert.That(shark.Items, Is.SameAs(items));
            Assert.That(shark.Items, Is.EqualTo(new string?[] { "clr-changed", null, "clr-only", "after takeover" }));
            AssertItemsOutput(shark, format, expected);

            shark.Patch.AppendNull("$.items"u8);
            AssertItemsOutput(shark, format,
                appendNull ? """["received",null,null,null]""" : """["received",null,"raw",null]""");
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_CurrentRawReplacementIsTheAppendBase(
            [Values("J", "W")] string format, [Values(false, true)] bool received)
        {
            var shark = received ? ReadItemsShark("""["received",null]""") : new MyShark("fresh", 4);
            var items = new List<string?> { "live-suppressed" };
            shark.Items = items;
            shark.Patch.Set("$.items"u8, """["raw-base"]"""u8);
            shark.Patch.Append("$.items"u8, "raw");
            AssertItemsOutput(shark, format, """["raw-base","raw"]""");
            shark.Patch.AppendNull("$.items"u8);
            AssertItemsOutput(shark, format, """["raw-base","raw",null]""");
            Assert.That(shark.Items, Is.SameAs(items));
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_UntouchedRegistrationAndWritesDoNotMaterialize(
            [Values("J", "W")] string inputFormat, [Values(false, true)] bool streaming)
        {
            _ = new MyShark("registration", 0).Items;
            var shark = ReadItemsShark("""["received",null]""", inputFormat, streaming);
            Assert.That(GetItemsState(shark), Is.Null);
            foreach (string format in new[] { "J", "W" })
            {
                AssertItemsOutput(shark, format, """["received",null]""");
                Assert.That(GetItemsState(shark), Is.Null, "The writer must not create collection state.");
            }
            var items = shark.Items;
            Assert.That(items, Is.EqualTo(new string?[] { "received", null }));
            Assert.That(shark.Items, Is.SameAs(items));
            AssertItemsOutput(shark, "W", """["received",null]""");
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_MissingReceivedNullAndAssignedNullRemainDistinct(
            [Values("J", "W")] string format, [Values(null, "null", "[]")] string? received)
        {
            _ = new MyShark("registration", 0).Items;
            var shark = ReadItemsShark(received);
            AssertItemsOutput(shark, format, received);
            if (received == "[]")
                Assert.That(shark.Items, Is.Empty);
            else
                Assert.That(shark.Items, Is.Null);
            AssertItemsOutput(shark, format, received);
            shark.Items = null;
            Assert.That(shark.Items, Is.Null);
            AssertItemsOutput(shark, format, null);

            shark.Items = new List<string?> { null };
            AssertItemsOutput(shark, format, "[null]");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_AssignmentBeforeGetterDoesNotMaterializeOldJson(string format)
        {
            var replacement = new List<string?> { "assigned", null };
            var shark = ReadItemsShark("""{"not":"an array"}""");
            shark.Items = replacement;
            replacement.Add("later mutation");
            Assert.That(shark.Items, Is.SameAs(replacement));
            AssertItemsOutput(shark, format, """["assigned",null,"later mutation"]""");

            var cleared = ReadItemsShark("""{"not":"an array"}""");
            cleared.Items = null;
            Assert.That(cleared.Items, Is.Null);
            AssertItemsOutput(cleared, format, null);
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_FirstGetterUsesCurrentRawValueAndDoesNotResync(
            [Values("J", "W")] string format, [Values("replace", "remove", "null", "element")] string edit)
        {
            var shark = ReadItemsShark("""["received",null]""");
            string? expected;
            switch (edit)
            {
                case "replace":
                    shark.Patch.Set("$.items"u8, """["raw"]"""u8);
                    expected = """["raw"]""";
                    break;
                case "remove":
                    shark.Patch.Remove("$.items"u8);
                    expected = null;
                    break;
                case "null":
                    shark.Patch.SetNull("$.items"u8);
                    expected = "null";
                    break;
                default:
                    shark.Patch.Set("$.items[0]"u8, "raw");
                    expected = """["raw",null]""";
                    break;
            }
            var first = shark.Items;
            Assert.That(first, expected is null or "null" ? Is.Null
                : Is.EqualTo(edit == "element" ? new string?[] { "raw", null } : new[] { "raw" }));
            AssertItemsOutput(shark, format, expected);
            var replacement = new List<string?> { "later CLR assignment" };
            shark.Items = replacement;
            Assert.That(shark.Items, Is.SameAs(replacement));
            AssertItemsOutput(shark, format, expected, "Typed assignment does not clear explicit raw ownership.");

            shark.Patch.Set("$.items"u8, """["later raw"]"""u8);
            Assert.That(shark.Items, Is.SameAs(replacement));
            replacement.Add("still CLR-only");
            AssertItemsOutput(shark, format, """["later raw"]""");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_InvalidMaterializationDoesNotCachePartialState(string format)
        {
            _ = new MyShark("registration", 0).Items;
            var shark = ReadItemsShark("""["valid",42]""");
            AssertItemsOutput(shark, format, """["valid",42]""");
            Assert.Throws<InvalidOperationException>(() => _ = shark.Items);
            AssertItemsOutput(shark, format, """["valid",42]""");
            shark.Patch.Set("$.items"u8, """["replacement",null]"""u8);
            Assert.That(shark.Items, Is.EqualTo(new string?[] { "replacement", null }));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_RootOverrideBypassesTheRetainedWriter(string format)
        {
            var shark = new MyShark("live", 4) { Items = new List<string?> { "live" } };
            shark.Patch.Set("$"u8, """{"items":["root"],"root":true}"""u8);
            var output = AssertItemsOutput(shark, format, """["root"]""");
            Assert.That(output.EnumerateObject().Count(), Is.EqualTo(2));
            Assert.That(output.GetProperty("root").GetBoolean(), Is.True);
            Assert.That(shark.Items, Is.EqualTo(new[] { "live" }));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_NearPrefixAppendDoesNotClaimTheCollection(string format)
        {
            var shark = new MyShark("near-prefix", 4) { Items = new List<string?> { "live" } };
            shark.Patch.Append("$.itemsExtra"u8, "other");
            Assert.That(shark.Patch.Contains("$"u8, "items"u8), Is.False);
            var output = AssertItemsOutput(shark, format, """["live"]""");
            AssertCollectionProperty(output, "itemsExtra", """["other"]""");
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_UnclaimedReceivedArrayUsesTheExistingFallback(
            [Values("J", "W")] string format, [Values(false, true)] bool streaming)
        {
            var shark = ReadSourceShark(
                """{"name":"source","finCount":4,"unclaimedItems":["received",null]}""",
                new ModelReaderWriterOptions("W"), streaming);
            using (var untouched = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default)))
            {
                AssertCollectionProperty(untouched.RootElement, "unclaimedItems", """["received",null]""");
            }
            shark.Patch.Append("$.unclaimedItems"u8, "raw");
            using var appended = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default));
            AssertCollectionProperty(appended.RootElement, "unclaimedItems", """["received",null,"raw"]""");
        }

        [Test]
        [Category("ComposesCollection")]
        public void Items_SardOnlyFallbackDoesNotDuplicateTheFirstAppend(
            [Values("J", "W")] string format,
            [Values("unclaimedItems", "items.with.dot", "quote\"and\\slash")] string name)
        {
            var constructor = typeof(MyShark).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 5);
            var source = new Dictionary<string, BinaryData> { [name] = BinaryData.FromString("""["bag-only"]""") };
            var shark = (MyShark)constructor.Invoke(
                new object[] { "factory", 4, default(JsonPatch), source, default(ReadOnlyMemory<byte>) });
            var options = new ModelReaderWriterOptions(format);
            using (var original = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default)))
            {
                AssertCollectionProperty(original.RootElement, name, """["bag-only"]""");
            }
            byte[] path = Encoding.UTF8.GetBytes("$[" + JsonSerializer.Serialize(name) + "]");
            shark.Patch.Append(path, "raw");
            Assert.That(shark.Patch.Contains(path), Is.False);
            Assert.That(shark.Patch.Contains("$"u8, Encoding.UTF8.GetBytes(name)), Is.True);
            using (var first = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default)))
            {
                AssertCollectionProperty(first.RootElement, name, """["raw"]""");
            }
            shark.Patch.AppendNull(path);
            using var next = JsonDocument.Parse(ModelReaderWriter.Write(shark, options, SampleTypeSpecContext.Default));
            AssertCollectionProperty(next.RootElement, name, """["raw",null]""");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_SardOnlyUnregisteredItemsRespectRawOwnershipOnMixedTarget(string format)
        {
            var prototype = new NullableDynamicModel();
            var constructor = typeof(NullableDynamicModel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 10);
            var source = new Dictionary<string, BinaryData> { ["items"] = BinaryData.FromString("""["bag-only"]""") };
            var target = (NullableDynamicModel)constructor.Invoke(new object[]
            {
                null!, prototype.Children, prototype.ChildDictionary, prototype.NestedChildren,
                prototype.NestedChildDictionary, prototype.DictionaryChildren, prototype.ListOfDictionaries,
                default(JsonPatch), source, default(ReadOnlyMemory<byte>)
            });
            var options = new ModelReaderWriterOptions(format);
            target.Patch.Append("$.items"u8, "raw");
            Assert.That(target.Patch.Contains("$.items"u8), Is.False);
            using var output = JsonDocument.Parse(ModelReaderWriter.Write(target, options, SampleTypeSpecContext.Default));
            AssertCollectionProperty(output.RootElement, "items", """["raw"]""");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_ListHolderAcceptsAnInterfaceWithoutCopyingItsReference(string format)
        {
            var items = new Collection<string?> { "interface", null };
            var shark = new MyShark("interface", 4) { Items = items };
            Assert.That(shark.Items, Is.SameAs(items));
            items.Add("external");
            shark.Items[0] = "getter";
            AssertItemsOutput(shark, format, """["getter",null,"external"]""");
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_ListHolderPropertiesMaterializeAndMutateIndependently(string format)
        {
            _ = new MyShark("registration", 0).Items;
            var shark = ReadSourceShark(
                """{"name":"source","finCount":4,"items":["received",null],"itemScores":[1,null]}""",
                new ModelReaderWriterOptions("W"), streamingReader: true);
            Assert.That(GetItemsState(shark), Is.Null);
            var untouched = AssertItemsOutput(shark, format, """["received",null]""");
            AssertCollectionProperty(untouched, "itemScores", "[1,null]");
            Assert.That(GetItemsState(shark), Is.Null);

            var scores = GetItemScores(shark)!;
            Assert.That(GetItemScores(shark), Is.SameAs(scores));
            Assert.That(IsCollectionInitialized(shark, "ItemScores"), Is.True);
            Assert.That(IsCollectionInitialized(shark, "Items"), Is.False);
            scores[0] = 5;
            scores.Add(9);
            var scoresOnly = AssertItemsOutput(shark, format, """["received",null]""");
            AssertCollectionProperty(scoresOnly, "itemScores", "[5,null,9]");
            Assert.That(IsCollectionInitialized(shark, "Items"), Is.False);

            var items = shark.Items;
            items[0] = "live";
            items.Add("clr-only");
            var live = AssertItemsOutput(shark, format, """["live",null,"clr-only"]""");
            AssertCollectionProperty(live, "itemScores", "[5,null,9]");
            shark.Patch.Append("$.items"u8, "raw");
            scores.Add(10);
            var rawItems = AssertItemsOutput(shark, format, """["received",null,"raw"]""");
            AssertCollectionProperty(rawItems, "itemScores", "[5,null,9,10]");
            Assert.That(shark.Items, Is.SameAs(items));
            Assert.That(GetItemScores(shark), Is.SameAs(scores));
            Assert.That(shark.Patch.Contains("$"u8, "itemScores"u8), Is.False);

            shark.Patch.Append("$.itemScores"u8, 11);
            var rawBoth = AssertItemsOutput(shark, format, """["received",null,"raw"]""");
            AssertCollectionProperty(rawBoth, "itemScores", "[1,null,11]");
            Assert.That(GetItemScores(shark), Is.SameAs(scores));
            Assert.That(scores, Is.EqualTo(new int?[] { 5, null, 9, 10 }));
        }

        [TestCase("J")]
        [TestCase("W")]
        [Category("ComposesCollection")]
        public void Items_ListHolderPropertiesKeepAssignmentAndNullStatesIndependent(string format)
        {
            var shark = ReadSourceShark(
                """{"name":"source","finCount":4,"items":null,"itemScores":[1]}""",
                new ModelReaderWriterOptions("W"), streamingReader: true);
            Assert.That(shark.Items, Is.Null);
            var scores = new Collection<int?> { 2, null };
            SetItemScores(shark, scores);
            scores.Add(3);
            Assert.That(GetItemScores(shark), Is.SameAs(scores));
            var receivedNull = AssertItemsOutput(shark, format, "null");
            AssertCollectionProperty(receivedNull, "itemScores", "[2,null,3]");

            shark.Items = null;
            var assignedNull = AssertItemsOutput(shark, format, null);
            AssertCollectionProperty(assignedNull, "itemScores", "[2,null,3]");
            shark.Items = new List<string?> { "independent" };
            SetItemScores(shark, null);
            scores.Add(4);
            Assert.That(GetItemScores(shark), Is.Null);
            var scoresCleared = AssertItemsOutput(shark, format, """["independent"]""");
            AssertCollectionProperty(scoresCleared, "itemScores", null);

            var reverse = ReadSourceShark(
                """{"name":"source","finCount":4,"items":["received"],"itemScores":null}""",
                new ModelReaderWriterOptions("W"), streamingReader: true);
            Assert.That(GetItemScores(reverse), Is.Null);
            reverse.Items = new List<string?> { "assigned" };
            var scoresReceivedNull = AssertItemsOutput(reverse, format, """["assigned"]""");
            AssertCollectionProperty(scoresReceivedNull, "itemScores", "null");
            SetItemScores(reverse, null);
            var scoresAssignedNull = AssertItemsOutput(reverse, format, """["assigned"]""");
            AssertCollectionProperty(scoresAssignedNull, "itemScores", null);
        }

        private static IList<int?>? GetItemScores(MyShark shark)
            => (IList<int?>?)typeof(MySharkExtensions).GetMethod(
                "get_ItemScores", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { shark });

        private static void SetItemScores(MyShark shark, IList<int?>? scores)
            => typeof(MySharkExtensions).GetMethod("set_ItemScores", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object?[] { shark, scores });

        private static bool IsCollectionInitialized(MyShark shark, string name)
        {
            var state = GetItemsState(shark)!;
            object value = state.GetType().GetField(name)!.GetValue(state)!;
            return (bool)value.GetType().GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
        }

        private static MyShark ReadItemsShark(string? items, string format = "W", bool streaming = true)
        {
            string json = """{"name":"source","finCount":4,"future":{"keep":true}"""
                + (items is null ? "}" : ",\"items\":" + items + "}");
            var shark = ReadSourceShark(json, new ModelReaderWriterOptions(format), streaming);
            shark.Name = "declared-edited";
            return shark;
        }

        private static object? GetItemsState(MyShark shark)
        {
            object table = typeof(MySharkExtensions).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            object?[] arguments = [shark, null];
            return (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)! ? arguments[1] : null;
        }

        private static JsonElement AssertItemsOutput(MyShark shark, string format, string? expected, string? message = null)
        {
            string operations = shark.Patch.ToBinaryData().ToString();
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(
                shark, new ModelReaderWriterOptions(format), SampleTypeSpecContext.Default));
            AssertCollectionProperty(document.RootElement, "items", expected, message);
            Assert.That(shark.Patch.ToBinaryData().ToString(), Is.EqualTo(operations), "Writing must not synthesize raw operations.");
            return document.RootElement.Clone();
        }

        private static void AssertCollectionProperty(JsonElement root, string name, string? expected, string? message = null)
        {
            var values = root.EnumerateObject().Where(property => property.NameEquals(name))
                .Select(property => property.Value).ToArray();
            Assert.That(values, Has.Length.EqualTo(expected is null ? 0 : 1),
                $"{message} All decoded '{name}' values: {string.Join(" / ", values.Select(value => value.GetRawText()))}");
            if (expected is not null)
            {
                using var expectedDocument = JsonDocument.Parse(expected);
                Assert.That(JsonElement.DeepEquals(values[0], expectedDocument.RootElement), Is.True, message);
            }
        }

#pragma warning restore SCME0001
    }
}
