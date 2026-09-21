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

        private enum RawBenchmarkOperation { ShallowSet, DeepSet, Remove, Append }

        private sealed class RawBenchmarkPayload
        {
            public required string Name { get; init; }
            public required int UnknownFields { get; init; }
            public required int BarLength { get; init; }
            public required int Iterations { get; init; }
            public required BinaryData Source { get; init; }
            public required BinaryData Member { get; init; }
        }

        private sealed class RawBenchmarkOwner
        {
            private readonly MyShark? _model;
            private JsonPatch _legacy;

            public RawBenchmarkOwner(RawBenchmarkPayload payload, bool current)
            {
                if (current)
                {
                    _model = ModelReaderWriter.Read<MyShark>(
                        payload.Source, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
                }
                else
                {
                    _legacy = new JsonPatch(payload.Source.ToMemory());
                    _legacy.Set("$.tracker"u8, payload.Member);
                }
            }

            private ref JsonPatch Patch
            {
                get
                {
                    if (_model is not null)
                    {
                        return ref _model.Patch;
                    }
                    return ref _legacy;
                }
            }

            public void Edit(RawBenchmarkOperation operation, int ordinal, int ownerIndex)
            {
                int value = (ordinal + ownerIndex) % 2 + 1;
                switch (operation)
                {
                    case RawBenchmarkOperation.ShallowSet:
                        Patch.Set("$.tracker.raw"u8, value);
                        break;
                    case RawBenchmarkOperation.DeepSet:
                        Patch.Set("$.tracker.nested.value"u8, value);
                        break;
                    case RawBenchmarkOperation.Remove:
                        Patch.Remove(RemovePaths[ordinal]);
                        break;
                    case RawBenchmarkOperation.Append:
                        Patch.Append("$.tracker.items"u8, value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }
            }

            public ArrayBufferWriter<byte> Write()
            {
                if (_model is not null)
                {
                    return WriteBenchmarkTarget(_model);
                }
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    if (_legacy.Contains("$"u8))
                    {
                        writer.WriteRawValue(_legacy.GetJson("$"u8));
                        return buffer;
                    }
                    writer.WriteStartObject();
                    if (!_legacy.Contains("$.name"u8))
                    {
                        writer.WriteString("name"u8, "mako");
                    }
                    if (!_legacy.Contains("$.finCount"u8))
                    {
                        writer.WriteNumber("finCount"u8, 4);
                    }
                    _legacy.WriteTo(writer);
                    writer.WriteEndObject();
                }
                return buffer;
            }
        }

        private static readonly byte[][] RemovePaths =
        [
            "$.tracker.remove0"u8.ToArray(), "$.tracker.remove1"u8.ToArray(),
            "$.tracker.remove2"u8.ToArray(), "$.tracker.remove3"u8.ToArray(),
            "$.tracker.remove4"u8.ToArray(),
        ];

        [TestCase(0)]
        [TestCase(40)]
        [Category("ComposesRawBenchmarkValidation")]
        public void Shark_RawEditBenchmark_InputsAndBaselineEligibility(int fields)
        {
            var payload = CreateRawBenchmarkPayload("validation", fields, 5, 1);
            foreach (RawBenchmarkOperation operation in Enum.GetValues<RawBenchmarkOperation>())
            {
                foreach (int priorEdits in new[] { 0, 4 })
                {
                    bool equivalent = ValidateRawBenchmarkPair(payload, operation, priorEdits);
                    TestContext.Out.WriteLine($"{operation}, prior edits {priorEdits}: legacy equivalent = {equivalent}");
                    Assert.That(equivalent, Is.EqualTo(operation != RawBenchmarkOperation.Append),
                        "The legacy append output is invalid and must not be used as a timing baseline.");
                }
            }
        }

        [Test]
        [Explicit("Current raw-edit microbenchmark. Run alone in Release with DOTNET_TieredCompilation=0.")]
        [NonParallelizable]
        [Category("ComposesRawEditBenchmark")]
        public void Shark_CallbackRawEdits_Cost()
        {
#if DEBUG
            Assert.Fail("Run the raw-edit comparison in Release.");
#endif
            Assert.That(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Is.EqualTo("0"));
            bool smoke = Environment.GetEnvironmentVariable("COMPOSES_RAW_BENCHMARK_SMOKE") == "1";
            _ = new MyShark("registration", 0).Tracker;
            var shapes = new[]
            {
                (Name: "small", Fields: 0, BarLength: 5, Iterations: 512),
                (Name: "wide-40", Fields: 40, BarLength: 5, Iterations: 128),
                (Name: "wide-512", Fields: 512, BarLength: 5, Iterations: 32),
                (Name: "declared-string-1MiB", Fields: 0, BarLength: 1024 * 1024, Iterations: 8),
            };
            var results = new List<object>();
            foreach (var shape in smoke ? shapes.Take(1) : shapes)
            {
                var payload = CreateRawBenchmarkPayload(shape.Name, shape.Fields, shape.BarLength, shape.Iterations);
                foreach (RawBenchmarkOperation operation in Enum.GetValues<RawBenchmarkOperation>())
                {
                    foreach (int priorEdits in new[] { 0, 4 })
                    {
                        bool equivalent = ValidateRawBenchmarkPair(payload, operation, priorEdits);
                        bool[] variants = equivalent ? [true, false] : [true];
                        foreach (bool current in variants)
                        {
                            var warm = PrepareRawBenchmarkOwners(payload, current, operation, priorEdits, Math.Min(payload.Iterations, 16));
                            for (int i = 0; i < warm.Length; i++)
                            {
                                warm[i].Edit(operation, priorEdits, i);
                                _ = warm[i].Write();
                            }
                        }

                        const int trials = 7;
                        var editUs = variants.Select(_ => new double[trials]).ToArray();
                        var editBytes = variants.Select(_ => new double[trials]).ToArray();
                        var writeUs = variants.Select(_ => new double[trials]).ToArray();
                        var writeBytes = variants.Select(_ => new double[trials]).ToArray();
                        for (int trial = 0; trial < trials; trial++)
                        {
                            for (int offset = 0; offset < variants.Length; offset++)
                            {
                                int index = (trial + offset) % variants.Length;
                                var owners = PrepareRawBenchmarkOwners(
                                    payload, variants[index], operation, priorEdits, payload.Iterations);
                                CollectBenchmarkGarbage();
                                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                                long started = Stopwatch.GetTimestamp();
                                for (int i = 0; i < owners.Length; i++)
                                {
                                    owners[i].Edit(operation, priorEdits, i);
                                }
                                long elapsed = Stopwatch.GetTimestamp() - started;
                                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                                editUs[index][trial] = elapsed * 1_000_000.0 / Stopwatch.Frequency / owners.Length;
                                editBytes[index][trial] = allocated / (double)owners.Length;
                                AssertRawBenchmarkResult(owners[0].Write(), payload, operation, priorEdits, 0);
                                AssertRawBenchmarkResult(owners[^1].Write(), payload, operation, priorEdits, owners.Length - 1);

                                CollectBenchmarkGarbage();
                                long consumed = 0;
                                allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                                started = Stopwatch.GetTimestamp();
                                for (int i = 0; i < owners.Length; i++)
                                {
                                    consumed += owners[0].Write().WrittenCount;
                                }
                                elapsed = Stopwatch.GetTimestamp() - started;
                                allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                                writeUs[index][trial] = elapsed * 1_000_000.0 / Stopwatch.Frequency / owners.Length;
                                writeBytes[index][trial] = allocated / (double)owners.Length;
                                Assert.That(consumed, Is.GreaterThan(0));
                            }
                        }

                        results.Add(new
                        {
                            payload.Name,
                            payload.UnknownFields,
                            payload.BarLength,
                            SourceBytes = payload.Source.ToMemory().Length,
                            MemberBytes = payload.Member.ToMemory().Length,
                            payload.Iterations,
                            Trials = trials,
                            Operation = operation.ToString(),
                            PriorEdits = priorEdits,
                            Baseline = equivalent ? "Equivalent correct legacy output" : "INVALID legacy output: descendant append escapes to root; legacy not timed",
                            Variants = variants.Select((current, index) => new
                            {
                                Name = current ? "current-scoped-native" : "legacy-parked-unknown",
                                EditOnly = SummarizeRawBenchmark(editUs[index], editBytes[index]),
                                SteadySerializationAfterEdit = SummarizeRawBenchmark(writeUs[index], writeBytes[index]),
                            }).ToArray(),
                        });
                    }
                }
            }

            TestContext.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                GCSettings.IsServerGC,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                ClientModel = typeof(JsonPatch).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                Smoke = smoke,
                Setup = "Creation, deserialization, registration and prior edits excluded. Every timed edit has a separately prepared owner. PriorEdits=0 is first edit; PriorEdits=4 is fifth edit after four successful same-kind operations. Values alternate; Remove uses five distinct present keys. No repeated no-op removes.",
                Timing = "Edit-only includes one native public patch operation. Separately measured steady serialization repeats one already-edited owner. New buffers/writers are included only in serialization. Warmed and rotated seven trials; peak first-edit owner batches bounded (8 at 1 MiB).",
                Payload = "Actual MyShark generated sample; TrackerReference.bar is the declared string. Extra scalar fields, nested.value, items, raw and five removal fields are unknown to that child. The raw path does not materialize or merge the child.",
                Results = results,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static RawBenchmarkOwner[] PrepareRawBenchmarkOwners(
            RawBenchmarkPayload payload, bool current, RawBenchmarkOperation operation, int priorEdits, int count)
        {
            var owners = new RawBenchmarkOwner[count];
            for (int i = 0; i < count; i++)
            {
                owners[i] = new RawBenchmarkOwner(payload, current);
                for (int ordinal = 0; ordinal < priorEdits; ordinal++)
                {
                    owners[i].Edit(operation, ordinal, i);
                }
            }
            return owners;
        }

        private static bool ValidateRawBenchmarkPair(RawBenchmarkPayload payload, RawBenchmarkOperation operation, int priorEdits)
        {
            var candidate = PrepareRawBenchmarkOwners(payload, true, operation, priorEdits, 1)[0];
            var baseline = PrepareRawBenchmarkOwners(payload, false, operation, priorEdits, 1)[0];
            candidate.Edit(operation, priorEdits, 0);
            baseline.Edit(operation, priorEdits, 0);
            var actual = candidate.Write();
            var legacy = baseline.Write();
            AssertRawBenchmarkResult(actual, payload, operation, priorEdits, 0);
            using var actualDocument = JsonDocument.Parse(actual.WrittenMemory);
            using var legacyDocument = JsonDocument.Parse(legacy.WrittenMemory);
            bool equivalent = JsonElement.DeepEquals(actualDocument.RootElement, legacyDocument.RootElement)
                && GetRootPropertyCount(legacyDocument.RootElement, "tracker") == 1
                && legacyDocument.RootElement.EnumerateObject().Count() == 3;
            if (equivalent)
            {
                AssertRawBenchmarkResult(legacy, payload, operation, priorEdits, 0);
            }
            return equivalent;
        }

        private static void AssertRawBenchmarkResult(ArrayBufferWriter<byte> output, RawBenchmarkPayload payload,
            RawBenchmarkOperation operation, int priorEdits, int ownerIndex)
        {
            using var document = JsonDocument.Parse(output.WrittenMemory);
            var root = document.RootElement;
            Assert.That(root.EnumerateObject().Count(), Is.EqualTo(3), "No raw descendants may escape to the root.");
            Assert.That(GetRootPropertyCount(root, "tracker"), Is.EqualTo(1));
            var tracker = root.GetProperty("tracker");
            Assert.That(tracker.GetProperty("bar").GetString()!.Length, Is.EqualTo(payload.BarLength));
            Assert.That(tracker.EnumerateObject().Count(),
                Is.EqualTo(payload.UnknownFields + 9 - (operation == RawBenchmarkOperation.Remove ? priorEdits + 1 : 0)));
            int expectedValue = (priorEdits + ownerIndex) % 2 + 1;
            switch (operation)
            {
                case RawBenchmarkOperation.ShallowSet:
                    Assert.That(tracker.GetProperty("raw").GetInt32(), Is.EqualTo(expectedValue));
                    break;
                case RawBenchmarkOperation.DeepSet:
                    Assert.That(tracker.GetProperty("nested").GetProperty("value").GetInt32(), Is.EqualTo(expectedValue));
                    break;
                case RawBenchmarkOperation.Remove:
                    for (int i = 0; i <= priorEdits; i++)
                    {
                        Assert.That(tracker.TryGetProperty("remove" + i, out _), Is.False);
                    }
                    break;
                case RawBenchmarkOperation.Append:
                    var array = tracker.GetProperty("items");
                    Assert.That(array.GetArrayLength(), Is.EqualTo(3 + priorEdits));
                    Assert.That(array[0].GetInt32(), Is.EqualTo(1));
                    Assert.That(array[1].GetInt32(), Is.EqualTo(2));
                    for (int i = 0; i <= priorEdits; i++)
                    {
                        Assert.That(array[i + 2].GetInt32(), Is.EqualTo((i + ownerIndex) % 2 + 1));
                    }
                    break;
            }
        }

        private static RawBenchmarkPayload CreateRawBenchmarkPayload(string name, int fields, int barLength, int iterations)
        {
            var memberBuffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(memberBuffer))
            {
                writer.WriteStartObject();
                writer.WriteString("bar", new string('x', barLength));
                writer.WriteNumber("raw", 0);
                writer.WriteStartObject("nested");
                writer.WriteNumber("value", 0);
                writer.WriteEndObject();
                writer.WriteStartArray("items");
                writer.WriteNumberValue(1);
                writer.WriteNumberValue(2);
                writer.WriteEndArray();
                for (int i = 0; i < 5; i++)
                {
                    writer.WriteNumber("remove" + i, i);
                }
                for (int i = 0; i < fields; i++)
                {
                    writer.WriteString($"field{i:D4}", $"{i:D4}-{new string('x', 24)}");
                }
                writer.WriteEndObject();
            }
            var parentBuffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(parentBuffer))
            {
                writer.WriteStartObject();
                writer.WriteString("name", "mako");
                writer.WriteNumber("finCount", 4);
                writer.WritePropertyName("tracker");
                writer.WriteRawValue(memberBuffer.WrittenSpan);
                writer.WriteEndObject();
            }
            return new RawBenchmarkPayload
            {
                Name = name,
                UnknownFields = fields,
                BarLength = barLength,
                Iterations = iterations,
                Source = BinaryData.FromBytes(parentBuffer.WrittenMemory),
                Member = BinaryData.FromBytes(memberBuffer.WrittenMemory),
            };
        }

        private static object SummarizeRawBenchmark(double[] microseconds, double[] bytes) => new
        {
            MedianMicroseconds = Median(microseconds),
            MinMicroseconds = microseconds.Min(),
            MaxMicroseconds = microseconds.Max(),
            MedianAllocatedBytes = Median(bytes),
            TrialMicroseconds = microseconds,
            TrialAllocatedBytes = bytes,
        };

        private static void CollectBenchmarkGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

#pragma warning restore SCME0001
    }
}
