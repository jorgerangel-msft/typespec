// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// Hand-written stand-in for a helper the emitter would generate once per library to support the
// `composes` client option (https://github.com/microsoft/typespec/issues/11715).
//
// Every composed extension property is backed by the target's JsonPatch, and every accessor needs
// the same two pieces of care:
//
//   * Getters must check IsRemoved BEFORE TryGetValue/TryGetJson. After Remove, JsonPatch reports
//     TryGetValue == true with an empty value, so an unguarded getter returns "" (or throws, for
//     model-typed properties reading a zero-length buffer) instead of null.
//   * Setters must distinguish "assign null" from "unset", i.e. Remove rather than Set.
//
// Centralizing that here keeps the generated property bodies to single expressions and means the
// trap is handled in exactly one place instead of being re-emitted per property.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;

namespace SampleTypeSpec
{
    [Experimental("SCME0001")]
    internal static class ComposedPatchHelpers
    {
        internal static string GetStringOrDefault(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath)
            => !patch.IsRemoved(jsonPath) && patch.TryGetValue(jsonPath, out string value) ? value : null;

        internal static int GetInt32OrDefault(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath)
            => !patch.IsRemoved(jsonPath) && patch.TryGetValue(jsonPath, out int value) ? value : default;

        internal static float? GetNullableSingleOrDefault(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath)
            => !patch.IsRemoved(jsonPath) && patch.TryGetNullableValue(jsonPath, out float? value) ? value : null;

        internal static T GetJsonModelOrDefault<T>(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath)
            where T : class, IJsonModel<T>
        {
            // A removed key still reports TryGetJson == true, but hands back a zero-length buffer
            // that ModelReaderWriter.Read cannot parse.
            if (patch.IsRemoved(jsonPath)
                || !patch.TryGetJson(jsonPath, out ReadOnlyMemory<byte> json)
                || json.Length == 0)
            {
                return null;
            }
            return ModelReaderWriter.Read<T>(
                BinaryData.FromBytes(json),
                ModelSerializationExtensions.WireOptions,
                SampleTypeSpecContext.Default);
        }

        internal static void SetOrRemove(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath, string value)
        {
            if (value is null)
            {
                patch.Remove(jsonPath);
                return;
            }
            patch.Set(jsonPath, value);
        }

        internal static void SetOrRemove(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath, float? value)
        {
            if (value is null)
            {
                patch.Remove(jsonPath);
                return;
            }
            patch.Set(jsonPath, value.Value);
        }

        internal static void SetJsonModelOrRemove<T>(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath, T value)
            where T : class, IJsonModel<T>
        {
            if (value is null)
            {
                patch.Remove(jsonPath);
                return;
            }
            patch.Set(
                jsonPath,
                ModelReaderWriter.Write(value, ModelSerializationExtensions.WireOptions, SampleTypeSpecContext.Default));
        }
    }
}
