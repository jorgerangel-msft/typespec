// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001
    internal static class MixedComposedState<T> where T : class
    {
        internal sealed class Values
        {
            public ComposedModelValue<TrackerReference> Tracker;
            public ComposedModelValue<RoundTripModel> Profile;
        }
        internal static readonly ConditionalWeakTable<T, Values> Table = new();

        internal static void Write(Utf8JsonWriter writer, T target, ref JsonPatch patch, ModelReaderWriterOptions options)
        {
            Table.TryGetValue(target, out var state);
            if (!patch.Contains("$.tracker"u8))
            {
                (state?.Tracker ?? default).Write(writer, ref patch, "$.tracker"u8, "tracker"u8, options);
            }
            if (!patch.Contains("$.profile"u8))
            {
                (state?.Profile ?? default).Write(writer, ref patch, "$.profile"u8, "profile"u8, options);
            }
        }
    }
#pragma warning restore SCME0001
}
