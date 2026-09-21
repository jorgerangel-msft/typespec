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
#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

        [Test]
        [Explicit("Serialization microbenchmark: run this test alone in Release with DOTNET_TieredCompilation=0.")]
        [NonParallelizable]
        [Category("ComposesCallbackBenchmark")]
        public void Shark_CallbackComparison_SerializationCost()
        {
#if DEBUG
            Assert.Fail("Run the serialization comparison in Release.");
#endif
            Assert.That(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Is.EqualTo("0"));
            var results = new List<object>();
            foreach (var shape in new[]
            {
                (Name: "small", Fields: 0, StringLength: 5, Iterations: 4000),
                (Name: "wide-40", Fields: 40, StringLength: 5, Iterations: 500),
                (Name: "wide-512", Fields: 512, StringLength: 5, Iterations: 64),
                (Name: "declared-string-1MiB", Fields: 0, StringLength: 1024 * 1024, Iterations: 64),
            })
            {
                var child = CreateBenchmarkChild(shape.Fields, shape.StringLength, includeRawOnly: false);
                var equivalentChild = CreateBenchmarkChild(shape.Fields, shape.StringLength, includeRawOnly: true);
                var guarded = new MyShark("mako", 4) { Tracker = child };
                var overlay = new MyShark("mako", 4) { Tracker = child };
                overlay.Patch.Set("$.tracker.rawOnly"u8, "\"kept\""u8);
                UseLiveOverlayForComparison(overlay);

                var registration = typeof(MyShark).GetField("_composedWriters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var callbacks = (Delegate?)registration.GetType().GetField("Write")!.GetValue(registration);
                Assert.That(callbacks!.GetInvocationList(), Has.Length.EqualTo(1),
                    "Run only this test in a fresh process so other registration tests cannot affect the measurement.");
                var variants = new (string Name, Func<ArrayBufferWriter<byte>> Write)[]
                {
                    ("direct", () => WriteBenchmarkDirect(child)),
                    ("guarded-callback", () => WriteBenchmarkTarget(guarded)),
                    ("equivalent-direct", () => WriteBenchmarkDirect(equivalentChild)),
                    ("live-overlay", () => WriteBenchmarkTarget(overlay)),
                };
                AssertEquivalentBenchmarkOutputs(variants[0].Write(), variants[1].Write(), shape.Fields + 1);
                AssertEquivalentBenchmarkOutputs(variants[2].Write(), variants[3].Write(), shape.Fields + 2);

                foreach (var variant in variants)
                {
                    for (int i = 0; i < Math.Min(shape.Iterations, 128); i++)
                    {
                        _ = variant.Write();
                    }
                }

                const int trials = 7;
                var microseconds = new double[variants.Length][];
                var allocatedBytes = new double[variants.Length][];
                for (int i = 0; i < variants.Length; i++)
                {
                    microseconds[i] = new double[trials];
                    allocatedBytes[i] = new double[trials];
                }
                long consumedBytes = 0;
                for (int trial = 0; trial < trials; trial++)
                {
                    for (int offset = 0; offset < variants.Length; offset++)
                    {
                        int index = (trial + offset) % variants.Length;
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();
                        for (int iteration = 0; iteration < shape.Iterations; iteration++)
                        {
                            consumedBytes += variants[index].Write().WrittenCount;
                        }
                        long elapsed = Stopwatch.GetTimestamp() - started;
                        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                        microseconds[index][trial] = elapsed * 1_000_000.0 / Stopwatch.Frequency / shape.Iterations;
                        allocatedBytes[index][trial] = allocated / (double)shape.Iterations;
                    }
                }
                Assert.That(consumedBytes, Is.GreaterThan(0));
                AssertEquivalentBenchmarkOutputs(variants[2].Write(), variants[3].Write(), shape.Fields + 2);
                results.Add(new
                {
                    shape.Name,
                    shape.Fields,
                    shape.StringLength,
                    shape.Iterations,
                    Trials = trials,
                    OutputBytes = variants[2].Write().WrittenCount,
                    Variants = variants.Select((variant, index) => new
                    {
                        variant.Name,
                        MedianMicroseconds = Median(microseconds[index]),
                        MinMicroseconds = microseconds[index].Min(),
                        MaxMicroseconds = microseconds[index].Max(),
                        MedianAllocatedBytes = Median(allocatedBytes[index]),
                        TrialMicroseconds = microseconds[index],
                        TrialAllocatedBytes = allocatedBytes[index],
                    }).ToArray(),
                });
            }

            TestContext.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                GCSettings.IsServerGC,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                ClientModel = typeof(JsonPatch).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                Notes = "Actual MyShark/TrackerReference sample; warmed, rotated trials; new buffer/writer per write. Construction, deserialization and registration excluded. Overlay semantics are not equivalent to arbitrary raw edit replay.",
                Results = results,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static TrackerReference CreateBenchmarkChild(int fields, int stringLength, bool includeRawOnly)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("bar", new string('x', stringLength));
                for (int i = 0; i < fields; i++)
                {
                    writer.WriteString($"field{i:D4}", $"{i:D4}-{new string('x', 24)}");
                }
                if (includeRawOnly)
                {
                    writer.WriteString("rawOnly", "kept");
                }
                writer.WriteEndObject();
            }
            return ModelReaderWriter.Read<TrackerReference>(
                BinaryData.FromBytes(buffer.WrittenMemory), ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default)!;
        }

        private static ArrayBufferWriter<byte> WriteBenchmarkDirect(TrackerReference child)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("name"u8, "mako");
                writer.WriteNumber("finCount"u8, 4);
                writer.WritePropertyName("tracker"u8);
                ((IJsonModel<TrackerReference>)child).Write(writer, ModelReaderWriterOptions.Json);
                writer.WriteEndObject();
            }
            return buffer;
        }

        private static ArrayBufferWriter<byte> WriteBenchmarkTarget(MyShark target)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                ((IJsonModel<MyShark>)target).Write(writer, ModelReaderWriterOptions.Json);
            }
            return buffer;
        }

        private static void AssertEquivalentBenchmarkOutputs(ArrayBufferWriter<byte> expected, ArrayBufferWriter<byte> actual, int childProperties)
        {
            using var expectedDocument = JsonDocument.Parse(expected.WrittenMemory);
            using var actualDocument = JsonDocument.Parse(actual.WrittenMemory);
            Assert.That(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement), Is.True,
                "Timing comparisons require equivalent serialized output, not merely successful writes.");
            Assert.That(GetRootPropertyCount(actualDocument.RootElement, "tracker"), Is.EqualTo(1));
            Assert.That(actualDocument.RootElement.GetProperty("tracker").EnumerateObject().Count(), Is.EqualTo(childProperties));
        }

        private static double Median(double[] values) => values.OrderBy(value => value).ElementAt(values.Length / 2);

#pragma warning restore SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
    }
}
