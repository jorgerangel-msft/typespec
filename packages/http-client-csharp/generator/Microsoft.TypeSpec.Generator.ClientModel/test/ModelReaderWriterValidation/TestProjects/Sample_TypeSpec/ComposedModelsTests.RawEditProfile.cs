// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using NUnit.Framework;
using SampleTypeSpec;

namespace Microsoft.TypeSpec.Generator.ClientModel.Tests.ModelReaderWriterValidation.TestProjects.Sample_TypeSpec
{
    internal partial class ComposedModelsTests
    {
#pragma warning disable SCME0001

        private delegate string ProfileRootParser(ReadOnlySpan<byte> path, out int length);

        private static readonly ProfileRootParser ProfileParseRoot =
            typeof(MyShark).GetMethod("GetRootPropertyName", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<ProfileRootParser>();

        private static readonly Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions> ProfileCallback =
            typeof(MySharkExtensions).GetMethod("WriteComposedProperties", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Action<Utf8JsonWriter, MyShark, ModelReaderWriterOptions>>();

        private static readonly string[] ProfileStages =
            ["root-selector-validation", "current-raw-lookup", "local-patch-and-set", "raw-projection", "publish-inclusive"];

        private sealed class EditStageProbe
        {
            private readonly IDictionary<string, BinaryData> _received;
            public readonly MyShark Model;
            public readonly long[,] Ticks = new long[2, 5];
            public readonly long[,] Bytes = new long[2, 5];
            public readonly int[,] Calls = new int[2, 5];
            public int SetterCalls;
            public int ExactRootCalls;

            public EditStageProbe(MyShark model)
            {
                Model = model;
                _received = (IDictionary<string, BinaryData>)typeof(MyShark)
                    .GetField("_additionalBinaryDataProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                model.Patch.SetPropagators(Set, ProfileNoGet);
            }

            // Diagnostic copy of MyShark.PropagateSet. Only timing/counters are added.
            // Root parsing invokes the actual compiled private helper, not a replacement parser.
            private bool Set(ReadOnlySpan<byte> jsonPath, JsonPatch.EncodedValue value)
            {
                SetterCalls++;
                int frame = jsonPath.SequenceEqual("$.tracker"u8) ? 1 : 0;
                if (frame == 1)
                {
                    ExactRootCalls++;
                }
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long beforeTicks = Stopwatch.GetTimestamp();
                string name = ProfileParseRoot(jsonPath, out int rootLength);
                if (!_received.ContainsKey(name))
                {
                    throw new InvalidOperationException("The diagnostic payload must have this received member.");
                }
                if (name.IndexOfAny(new[] { '\'', '"', '\\' }) >= 0 || jsonPath.Slice(0, rootLength).IndexOf((byte)'\\') >= 0)
                {
                    throw new NotSupportedException("Escaped roots are outside this profiling task.");
                }
                Record(frame, 0, beforeTicks, beforeBytes);

                beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                beforeTicks = Stopwatch.GetTimestamp();
                ReadOnlySpan<byte> rootPath = jsonPath.Slice(0, rootLength);
                if (Model.Patch.IsRemoved(rootPath) || !Model.Patch.TryGetJson(rootPath, out ReadOnlyMemory<byte> current))
                {
                    throw new InvalidOperationException("The diagnostic must address a present raw member.");
                }
                Record(frame, 1, beforeTicks, beforeBytes);

                beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                beforeTicks = Stopwatch.GetTimestamp();
                var scoped = new JsonPatch(current);
                scoped.Set([.. "$"u8, .. jsonPath.Slice(rootLength)], value);
                bool ordinaryRootEdit = rootLength == jsonPath.Length && scoped.Contains("$"u8);
                Record(frame, 2, beforeTicks, beforeBytes);
                if (ordinaryRootEdit)
                {
                    return false;
                }

                beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                beforeTicks = Stopwatch.GetTimestamp();
                BinaryData replacement = scoped.ToBinaryData("J");
                Record(frame, 3, beforeTicks, beforeBytes);

                beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                beforeTicks = Stopwatch.GetTimestamp();
                Model.Patch.Set(rootPath, replacement);
                Record(frame, 4, beforeTicks, beforeBytes);
                return true;
            }

            private void Record(int frame, int stage, long beforeTicks, long beforeBytes)
            {
                Ticks[frame, stage] += Stopwatch.GetTimestamp() - beforeTicks;
                Bytes[frame, stage] += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                Calls[frame, stage]++;
            }
        }

        private sealed class ActualProfileCounter
        {
            private readonly JsonPatch.PropagatorSetter _setter;
            public int SetterCalls;
            public int RootSetterCalls;
            public int TrackerGets;
            public int BackupGets;
            public int BeaconGets;
            public int ProfileGets;
            public int OtherGets;

            public ActualProfileCounter(MyShark model)
            {
                _setter = typeof(MyShark).GetMethod("PropagateSet", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<JsonPatch.PropagatorSetter>(model);
                model.Patch.SetPropagators(Set, Get);
            }

            public void Clear()
            {
                SetterCalls = RootSetterCalls = TrackerGets = BackupGets = BeaconGets = ProfileGets = OtherGets = 0;
            }

            private bool Set(ReadOnlySpan<byte> path, JsonPatch.EncodedValue value)
            {
                SetterCalls++;
                if (path.SequenceEqual("$.tracker"u8))
                {
                    RootSetterCalls++;
                }
                return _setter(path, value);
            }

            private bool Get(ReadOnlySpan<byte> path, out JsonPatch.EncodedValue value)
            {
                if (path.SequenceEqual("$.tracker"u8))
                    TrackerGets++;
                else if (path.SequenceEqual("$.backupTracker"u8))
                    BackupGets++;
                else if (path.SequenceEqual("$.beacon"u8))
                    BeaconGets++;
                else if (path.SequenceEqual("$.profile"u8))
                    ProfileGets++;
                else
                    OtherGets++;
                value = default;
                return false;
            }
        }

        private sealed class ProfileLiveTracker : TrackerReference
        {
            public int Writes;
            public ProfileLiveTracker() : base("live") { }
            protected override void JsonModelWriteCore(Utf8JsonWriter writer, ModelReaderWriterOptions options)
            {
                Writes++;
                base.JsonModelWriteCore(writer, options);
            }
        }

        [Test]
        [Explicit("Stage attribution only. Release, DOTNET_TieredCompilation=0; not a production optimization.")]
        [NonParallelizable]
        [Category("ComposesRawEditProfile")]
        public void Shark_RawEditProfile_Stages()
        {
            AssertProfileConfiguration();
            _ = new MyShark("register", 0).Tracker;
            var results = new List<object>();
            foreach (int fields in new[] { 0, 512 })
            {
                var payload = CreateRawBenchmarkPayload(fields == 0 ? "small" : "wide-512", fields, 5, fields == 0 ? 512 : 64);
                foreach (int priorEdits in new[] { 0, 4 })
                {
                    var actualCounted = CreateProfileModel(payload, priorEdits);
                    var counter = new ActualProfileCounter(actualCounted);
                    var live = new ProfileLiveTracker();
                    using (var writer = new Utf8JsonWriter(new ArrayBufferWriter<byte>()))
                    {
                        ((IJsonModel<TrackerReference>)live).Write(writer, ModelReaderWriterOptions.Json);
                    }
                    Assert.That(live.Writes, Is.EqualTo(1), "Validate the child-serializer counter before using its zero result.");
                    live.Writes = 0;
                    actualCounted.Tracker = live;
                    actualCounted.Patch.Set("$.tracker.raw"u8, 1);
                    Assert.That(counter.SetterCalls, Is.EqualTo(2));
                    Assert.That(counter.RootSetterCalls, Is.EqualTo(1));
                    Assert.That(live.Writes, Is.Zero);
                    _ = WriteBenchmarkTarget(actualCounted);
                    Assert.That(live.Writes, Is.Zero);

                    for (int i = 0; i < 32; i++)
                    {
                        var actual = CreateProfileModel(payload, priorEdits);
                        var probe = new EditStageProbe(CreateProfileModel(payload, priorEdits));
                        actual.Patch.Set("$.tracker.raw"u8, 1);
                        probe.Model.Patch.Set("$.tracker.raw"u8, 1);
                        AssertProfileEqual(actual, probe.Model);
                    }
                    const int trials = 7;
                    var actualUs = new double[trials];
                    var actualBytes = new double[trials];
                    var probeUs = new double[trials];
                    var probeBytes = new double[trials];
                    var stageUs = new double[2, 5, trials];
                    var stageBytes = new double[2, 5, trials];
                    var stageCalls = new double[2, 5, trials];
                    for (int trial = 0; trial < trials; trial++)
                    {
                        for (int offset = 0; offset < 2; offset++)
                        {
                            bool instrumented = (trial + offset) % 2 == 0;
                            var models = Enumerable.Range(0, payload.Iterations)
                                .Select(_ => CreateProfileModel(payload, priorEdits)).ToArray();
                            var probes = instrumented ? models.Select(model => new EditStageProbe(model)).ToArray() : [];
                            CollectBenchmarkGarbage();
                            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                            long started = Stopwatch.GetTimestamp();
                            for (int i = 0; i < models.Length; i++)
                            {
                                models[i].Patch.Set("$.tracker.raw"u8, i % 2 + 1);
                            }
                            long elapsed = Stopwatch.GetTimestamp() - started;
                            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                            (instrumented ? probeUs : actualUs)[trial] = elapsed * 1_000_000.0 / Stopwatch.Frequency / models.Length;
                            (instrumented ? probeBytes : actualBytes)[trial] = allocated / (double)models.Length;
                            if (instrumented)
                            {
                                foreach (var probe in probes)
                                {
                                    Assert.That(probe.SetterCalls, Is.EqualTo(2));
                                    for (int frame = 0; frame < 2; frame++)
                                    {
                                        for (int stage = 0; stage < 5; stage++)
                                        {
                                            stageUs[frame, stage, trial] += probe.Ticks[frame, stage] * 1_000_000.0 / Stopwatch.Frequency / models.Length;
                                            stageBytes[frame, stage, trial] += probe.Bytes[frame, stage] / (double)models.Length;
                                            stageCalls[frame, stage, trial] += probe.Calls[frame, stage] / (double)models.Length;
                                        }
                                    }
                                }
                            }
                            AssertRawBenchmarkResult(WriteBenchmarkTarget(models[0]), payload, RawBenchmarkOperation.ShallowSet, priorEdits, 0);
                        }
                    }
                    var stages = new List<object>();
                    for (int frame = 0; frame < 2; frame++)
                    {
                        for (int stage = 0; stage < 5; stage++)
                        {
                            stages.Add(new
                            {
                                Frame = frame == 0 ? "outer-descendant" : "recursive-root",
                                Stage = ProfileStages[stage],
                                Calls = Enumerable.Range(0, trials).Select(t => stageCalls[frame, stage, t]).ToArray(),
                                Metrics = SummarizeRawBenchmark(
                                    Enumerable.Range(0, trials).Select(t => stageUs[frame, stage, t]).ToArray(),
                                    Enumerable.Range(0, trials).Select(t => stageBytes[frame, stage, t]).ToArray()),
                            });
                        }
                    }
                    results.Add(new
                    {
                        payload.Name,
                        SourceBytes = payload.Source.ToMemory().Length,
                        MemberBytes = payload.Member.ToMemory().Length,
                        payload.Iterations,
                        PriorEdits = priorEdits,
                        ActualSetterCalls = counter.SetterCalls,
                        ActualRootSetterCalls = counter.RootSetterCalls,
                        LiveChildWrites = live.Writes,
                        Actual = SummarizeRawBenchmark(actualUs, actualBytes),
                        DiagnosticCopy = SummarizeRawBenchmark(probeUs, probeBytes),
                        Stages = stages,
                    });
                }
            }
            WriteProfileReport("edit-stages", results,
                "Diagnostic copy adds clocks/counters only and invokes actual root parser. Native operations and output are checked against the unchanged target. Publish stage is INCLUSIVE of recursive-root stages; do not add both. Stage times include timer/counter overhead; allocations are the stronger attribution signal.");
        }

        [Test]
        [Explicit("Serialization attribution only. Release, DOTNET_TieredCompilation=0.")]
        [NonParallelizable]
        [Category("ComposesRawEditProfile")]
        public void Shark_RawEditProfile_SerializationLookups()
        {
            AssertProfileConfiguration();
            _ = new MyShark("register", 0).Tracker;
            var results = new List<object>();
            foreach (int fields in new[] { 0, 512 })
            {
                var payload = CreateRawBenchmarkPayload(fields == 0 ? "small" : "wide-512", fields, 5, fields == 0 ? 4096 : 256);
                var model = CreateProfileModel(payload, priorEdits: 1);
                var counted = CreateProfileModel(payload, priorEdits: 1);
                var counter = new ActualProfileCounter(counted);
                _ = WriteBenchmarkTarget(counted);
                Assert.That(counter.SetterCalls, Is.Zero, "Serialization must not invoke the edit helper.");
                Assert.That(counter.TrackerGets, Is.Zero, "An explicit tracker override skips the live/source callback branch.");
                Assert.That(counter.BackupGets, Is.EqualTo(1));
                Assert.That(counter.BeaconGets, Is.EqualTo(1));
                Assert.That(counter.ProfileGets, Is.EqualTo(1));
                Assert.That(counter.OtherGets, Is.Zero);

                var initialized = CreateProfileModel(payload, priorEdits: 1);
                initialized.BackupTracker = null;
                initialized.Beacon = null;
                initialized.Profile = null;
                var initializedCounter = new ActualProfileCounter(initialized);
                _ = WriteBenchmarkTarget(initialized);
                Assert.That(initializedCounter.BackupGets + initializedCounter.BeaconGets + initializedCounter.ProfileGets, Is.Zero);
                initialized.Patch.SetPropagators(
                    typeof(MyShark).GetMethod("PropagateSet", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .CreateDelegate<JsonPatch.PropagatorSetter>(initialized), ProfileNoGet);

                var minimalSource = BinaryData.FromString("""{"name":"mako","finCount":4}""");
                var minimalSeed = ModelReaderWriter.Read<MyShark>(
                    minimalSource,
                    ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
                minimalSeed.Patch.Set("$.tracker"u8, model.Patch.GetJson("$.tracker"u8));
                AssertProfileEqual(model, initialized);
                AssertProfileEqual(model, minimalSeed);
                using (var actual = JsonDocument.Parse(WriteBenchmarkTarget(model).WrittenMemory))
                using (var withoutCallback = JsonDocument.Parse(ProfileWriteOnlyPatch(model).WrittenMemory))
                {
                    Assert.That(JsonElement.DeepEquals(actual.RootElement, withoutCallback.RootElement), Is.True);
                    Assert.That(GetRootPropertyCount(withoutCallback.RootElement, "tracker"), Is.EqualTo(1));
                }

                var sink = new ArrayBufferWriter<byte>();
                using var sinkWriter = new Utf8JsonWriter(sink);
                sinkWriter.WriteStartObject();
                long consumed = 0;
                var received = (IDictionary<string, BinaryData>)typeof(MyShark)
                    .GetField("_additionalBinaryDataProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                var registration = typeof(MyShark).GetField("_composedWriters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var propertyNames = (HashSet<string>)registration.GetType()
                    .GetField("PropertyNames")!.GetValue(registration)!;
                var variants = new (string Name, Action Run)[]
                {
                    ("actual-full-serialization", () => consumed += WriteBenchmarkTarget(model).WrittenCount),
                    ("actual-same-output-absent-properties-initialized-null", () => consumed += WriteBenchmarkTarget(initialized).WrittenCount),
                    ("actual-same-output-minimal-original-seed", () => consumed += WriteBenchmarkTarget(minimalSeed).WrittenCount),
                    ("diagnostic-same-output-patch-writer-only", () => consumed += ProfileWriteOnlyPatch(model).WrittenCount),
                    ("actual-private-callback-only", () => ProfileCallback(sinkWriter, model, ModelReaderWriterOptions.Json)),
                    ("actual-Contains-tracker-only", () => consumed += model.Patch.Contains("$.tracker"u8) ? 1 : 0),
                    ("actual-Contains-absent-backup-only", () => consumed += model.Patch.Contains("$.backupTracker"u8) ? 1 : 0),
                    ("actual-TryGetJson-absent-backup-only", () => consumed += model.Patch.TryGetJson("$.backupTracker"u8, out _) ? 1 : 0),
                    ("actual-TryGetJson-absent-beacon-only", () => consumed += model.Patch.TryGetJson("$.beacon"u8, out _) ? 1 : 0),
                    ("actual-TryGetJson-absent-profile-only", () => consumed += model.Patch.TryGetJson("$.profile"u8, out _) ? 1 : 0),
                    ("minimal-seed-TryGetJson-absent-backup-only", () => consumed += minimalSeed.Patch.TryGetJson("$.backupTracker"u8, out _) ? 1 : 0),
                    ("actual-source-dictionary-enumeration-only", () =>
                    {
                        foreach (var property in received)
                        {
                            consumed += propertyNames.Contains(property.Key) ? 1 : 0;
                        }
                    }),
                };
                foreach (var variant in variants)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        variant.Run();
                    }
                }
                const int trials = 7;
                var microseconds = variants.Select(_ => new double[trials]).ToArray();
                var allocations = variants.Select(_ => new double[trials]).ToArray();
                for (int trial = 0; trial < trials; trial++)
                {
                    for (int offset = 0; offset < variants.Length; offset++)
                    {
                        int index = (trial + offset) % variants.Length;
                        CollectBenchmarkGarbage();
                        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();
                        for (int i = 0; i < payload.Iterations; i++)
                        {
                            variants[index].Run();
                        }
                        long elapsed = Stopwatch.GetTimestamp() - started;
                        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                        microseconds[index][trial] = elapsed * 1_000_000.0 / Stopwatch.Frequency / payload.Iterations;
                        allocations[index][trial] = allocated / (double)payload.Iterations;
                    }
                }
                Assert.That(sinkWriter.BytesCommitted + sinkWriter.BytesPending, Is.EqualTo(1),
                    "The measured callback writes nothing: tracker is overridden and the other three members are absent.");
                Assert.That(consumed, Is.GreaterThan(0));
                results.Add(new
                {
                    payload.Name,
                    OriginalSeedBytes = payload.Source.ToMemory().Length,
                    MinimalSeedBytes = minimalSource.ToMemory().Length,
                    payload.Iterations,
                    ActualGetterCallbackCounts = new
                    {
                        counter.TrackerGets,
                        counter.BackupGets,
                        counter.BeaconGets,
                        counter.ProfileGets,
                        counter.OtherGets,
                        counter.SetterCalls,
                    },
                    SourceLinkedCallsPerFullWrite = new
                    {
                        TargetContains = 3,
                        ComposedContains = 4,
                        TryGetJson = 3,
                        GetJson = 0,
                        SourceDictionaryEntries = 1,
                        SourceEntriesSkippedAsRegistered = 1,
                        UnclaimedSourceWrites = 0,
                        PatchWriteTo = 1,
                    },
                    Variants = variants.Select((variant, index) => new
                    {
                        variant.Name,
                        Metrics = SummarizeRawBenchmark(microseconds[index], allocations[index]),
                    }).ToArray(),
                });
            }
            WriteProfileReport("serialization-lookups", results,
                "Getter counts are observed by wrapping the actual native propagator getter (same false result). Contains counts are the straight-line current generated call sites. Timings use uncounted actual model/callback/native calls. Equal-output diagnostic isolation initializes only absent composed properties or substitutes a tiny original seed; production is unchanged. No native byte-visit instrumentation or inferred exact token count is claimed.");
        }

        private static ArrayBufferWriter<byte> ProfileWriteOnlyPatch(MyShark model)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                if (model.Patch.Contains("$"u8))
                {
                    writer.WriteRawValue(model.Patch.GetJson("$"u8));
                    return buffer;
                }
                writer.WriteStartObject();
                if (!model.Patch.Contains("$.name"u8))
                {
                    writer.WriteString("name"u8, model.Name);
                }
                if (!model.Patch.Contains("$.finCount"u8))
                {
                    writer.WriteNumber("finCount"u8, model.FinCount);
                }
                model.Patch.WriteTo(writer);
                writer.WriteEndObject();
            }
            return buffer;
        }

        private static MyShark CreateProfileModel(RawBenchmarkPayload payload, int priorEdits)
        {
            var model = ModelReaderWriter.Read<MyShark>(payload.Source, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
            for (int i = 0; i < priorEdits; i++)
            {
                model.Patch.Set("$.tracker.raw"u8, i % 2 + 1);
            }
            return model;
        }

        private static void AssertProfileEqual(MyShark expected, MyShark actual)
        {
            using var expectedDocument = JsonDocument.Parse(WriteBenchmarkTarget(expected).WrittenMemory);
            using var actualDocument = JsonDocument.Parse(WriteBenchmarkTarget(actual).WrittenMemory);
            Assert.That(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement), Is.True);
            Assert.That(GetRootPropertyCount(actualDocument.RootElement, "tracker"), Is.EqualTo(1));
        }

        private static bool ProfileNoGet(ReadOnlySpan<byte> path, out JsonPatch.EncodedValue value)
        {
            value = default;
            return false;
        }

        private static void AssertProfileConfiguration()
        {
#if DEBUG
            Assert.Fail("Run diagnostic attribution in Release.");
#endif
            Assert.That(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Is.EqualTo("0"));
        }

        private static void WriteProfileReport(string kind, object results, string notes)
            => TestContext.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Kind = kind,
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                GCSettings.IsServerGC,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                ClientModel = typeof(JsonPatch).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                Notes = notes,
                Results = results,
            }, new JsonSerializerOptions { WriteIndented = true }));

#pragma warning restore SCME0001
    }
}
