// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// PATTERN 3 of 3: composes target with INPUT + OUTPUT (round-trip) usage.
//
// MyShark's TCGC usage is `Input,Output,Json`. Simulates:
//
//   @clientOption("composes", MyShark, "csharp")
//   model MySharkOptions {
//     tag: string;
//     tracker: TrackerReference;
//     items?: (string | null)[];
//   }
//
// Write-only callback prototype on stock System.ClientModel 1.15.
// Ordinary children retain identity in a ConditionalWeakTable; no child JsonPatch is required.
// The target needs accessible unknown JSON and a write hook, but no composing-library read hook.
//
// The target captures additional binary data only for non-W reads, independently of registration.
// The patch seed still retains the original JSON; registered names are used only when writing.
// Contains guards explicit patch entries, not received values. Untouched properties preserve
// received JSON without materialization; explicitly assigned null means omitted.
// The target projects supported raw descendant edits into whole-member overrides. Once a
// member is patch-owned, the guard suppresses all live CLR values under it, not just edited leaves.
// Retained children use their JSON/persistence representation for either enclosing format.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

    /// <summary> Extension properties contributed to <see cref="MyShark"/>. </summary>
    public static partial class MySharkExtensions
    {
        private static readonly byte[] TrackerPath = "$.tracker"u8.ToArray();
        private static readonly byte[] BackupTrackerPath = "$.backupTracker"u8.ToArray();
        private static readonly byte[] BeaconPath = "$.beacon"u8.ToArray();
        private static readonly byte[] ProfilePath = "$.profile"u8.ToArray();
        private static readonly byte[] ItemsPath = "$.items"u8.ToArray();
        private static readonly byte[] ItemScoresPath = "$.itemScores"u8.ToArray();
        private static readonly Func<JsonElement, string> ReadStringItem = static element => element.GetString();
        private static readonly Action<Utf8JsonWriter, string> WriteStringItem = static (writer, value) => writer.WriteStringValue(value);
        private static readonly Func<JsonElement, int?> ReadNullableInt32Item =
            static element => element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();
        private static readonly Action<Utf8JsonWriter, int?> WriteNullableInt32Item = static (writer, value) =>
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
            }
            else
            {
                writer.WriteNullValue();
            }
        };

        private sealed class ComposedState
        {
            public ComposedModelValue<TrackerReference> Tracker;
            public ComposedModelValue<TrackerReference> BackupTracker;
            public ComposedModelValue<AnotherDynamicModel> Beacon;
            public ComposedModelValue<RoundTripModel> Profile;
            public ComposedListValue<string> Items;
            public ComposedListValue<int?> ItemScores;
            public bool UseLiveOverlayForComparison;
        }

        private static readonly ConditionalWeakTable<MyShark, ComposedState> States = new ConditionalWeakTable<MyShark, ComposedState>();

        // Accessing an extension initializes this class before it can retain a child.
        // Until then the target's ordinary unknown-data storage is sufficient.
        static MySharkExtensions()
        {
            MyShark.AddComposedWriter(WriteComposedProperties, "tracker", "backupTracker", "beacon", "profile", "items", "itemScores");
        }

        private static void WriteComposedProperties(Utf8JsonWriter writer, MyShark shark, ModelReaderWriterOptions options)
        {
            States.TryGetValue(shark, out ComposedState state);

            // Append entries own the property even when exact-path Contains is false.
            if (!shark.Patch.Contains("$"u8, "items"u8))
            {
                (state?.Items ?? default).Write(writer, ref shark.Patch, ItemsPath, "items"u8, WriteStringItem);
            }
            if (!shark.Patch.Contains("$"u8, "itemScores"u8))
            {
                (state?.ItemScores ?? default).Write(writer, ref shark.Patch, ItemScoresPath, "itemScores"u8, WriteNullableInt32Item);
            }

            if (state is not null && state.UseLiveOverlayForComparison)
            {
                shark.Patch.WriteComposedModelWithLiveOverlay(writer, TrackerPath, "tracker"u8, state.Tracker.Value, options);
                shark.Patch.WriteComposedModelWithLiveOverlay(writer, BackupTrackerPath, "backupTracker"u8, state.BackupTracker.Value, options);
                shark.Patch.WriteComposedModelWithLiveOverlay(writer, BeaconPath, "beacon"u8, state.Beacon.Value, options);
                shark.Patch.WriteComposedModelWithLiveOverlay(writer, ProfilePath, "profile"u8, state.Profile.Value, options);
                return;
            }

            if (!shark.Patch.Contains(TrackerPath))
            {
                (state?.Tracker ?? default).Write(writer, ref shark.Patch, TrackerPath, "tracker"u8, options);
            }
            if (!shark.Patch.Contains(BackupTrackerPath))
            {
                (state?.BackupTracker ?? default).Write(writer, ref shark.Patch, BackupTrackerPath, "backupTracker"u8, options);
            }
            if (!shark.Patch.Contains(BeaconPath))
            {
                (state?.Beacon ?? default).Write(writer, ref shark.Patch, BeaconPath, "beacon"u8, options);
            }
            if (!shark.Patch.Contains(ProfilePath))
            {
                (state?.Profile ?? default).Write(writer, ref shark.Patch, ProfilePath, "profile"u8, options);
            }
        }

        extension(MyShark shark)
        {
            /// <summary> Gets or sets the Tag. </summary>
            [Experimental("SCME0001")]
            public string Tag
            {
                get => shark.Patch.GetStringOrDefault("$.tag"u8);
                set => shark.Patch.SetOrRemove("$.tag"u8, value);
            }

            /// <summary> Gets or sets the Tracker. </summary>
            [Experimental("SCME0001")]
            public TrackerReference Tracker
            {
                get => States.GetOrCreateValue(shark).Tracker.GetValue(ref shark.Patch, TrackerPath);
                set => States.GetOrCreateValue(shark).Tracker.Value = value;
            }

            /// <summary> Gets or sets the BackupTracker -- same model type as Tracker. </summary>
            [Experimental("SCME0001")]
            public TrackerReference BackupTracker
            {
                get => States.GetOrCreateValue(shark).BackupTracker.GetValue(ref shark.Patch, BackupTrackerPath);
                set => States.GetOrCreateValue(shark).BackupTracker.Value = value;
            }

            /// <summary>
            /// Gets or sets the Beacon. Its type is itself a dynamic model -- it owns a JsonPatch
            /// and exposes a get-only, constructor-initialized property.
            /// </summary>
            [Experimental("SCME0001")]
            public AnotherDynamicModel Beacon
            {
                get => States.GetOrCreateValue(shark).Beacon.GetValue(ref shark.Patch, BeaconPath);
                set => States.GetOrCreateValue(shark).Beacon.Value = value;
            }

            /// <summary> Gets or sets an ordinary model with nested models and mutable collections. </summary>
            [Experimental("SCME0001")]
            public RoundTripModel Profile
            {
                get => States.GetOrCreateValue(shark).Profile.GetValue(ref shark.Patch, ProfilePath);
                set => States.GetOrCreateValue(shark).Profile.Value = value;
            }

            /// <summary> Gets or sets a retained list of strings, including null elements. </summary>
            /// <remarks>
            /// List mutations remain live until an explicit raw edit owns the whole property.
            /// Raw appends use received/current raw JSON, not this list. Assigning null omits
            /// the property unless an explicit raw value or removal takes precedence.
            /// </remarks>
            [Experimental("SCME0001")]
            public IList<string> Items
            {
                get => States.GetOrCreateValue(shark).Items.GetValue(ref shark.Patch, ItemsPath, ReadStringItem);
                set => States.GetOrCreateValue(shark).Items.Value = value;
            }

            // Internal proof of independent list state with a different element schema.
            internal IList<int?> ItemScores
            {
                get => States.GetOrCreateValue(shark).ItemScores.GetValue(ref shark.Patch, ItemScoresPath, ReadNullableInt32Item);
                set => States.GetOrCreateValue(shark).ItemScores.Value = value;
            }
        }
    }

#pragma warning restore SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
}
