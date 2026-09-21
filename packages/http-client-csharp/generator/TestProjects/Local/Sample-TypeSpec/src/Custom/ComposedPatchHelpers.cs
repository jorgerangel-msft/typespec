// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// Hand-written stand-in for a helper the emitter would generate once per library to support the
// `composes` client option (https://github.com/microsoft/typespec/issues/11715).
//
// Scalar composed properties use the target's JsonPatch; model properties retain actual objects
// and materialize unknown JSON lazily. Patch accessors need the same two pieces of care:
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
using System.Buffers;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

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
            => patch.GetJsonModelOrDefault<T>(jsonPath, out _);

        internal static T GetJsonModelOrDefault<T>(this ref JsonPatch patch, ReadOnlySpan<byte> jsonPath, out bool hasValue)
            where T : class, IJsonModel<T>
        {
            // A removed key still reports TryGetJson == true, but hands back a zero-length buffer
            // that ModelReaderWriter.Read cannot parse.
            if (patch.IsRemoved(jsonPath)
                || !patch.TryGetJson(jsonPath, out ReadOnlyMemory<byte> json)
                || json.Length == 0)
            {
                hasValue = false;
                return null;
            }
            hasValue = true;
            // Materialization uses JSON/persistence; the writer independently chooses the output format.
            return ModelReaderWriter.Read<T>(
                BinaryData.FromBytes(json),
                ModelReaderWriterOptions.Json,
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
                ModelReaderWriter.Write(value, ModelReaderWriterOptions.Json, SampleTypeSpecContext.Default));
        }

        internal static void WriteJsonModel<T>(Utf8JsonWriter writer, ReadOnlySpan<byte> propertyName, T value, ModelReaderWriterOptions options)
            where T : class, IJsonModel<T>
        {
            if (value is not null)
            {
                writer.WritePropertyName(propertyName);
                value.Write(writer, options);
            }
        }

        internal static void WriteRawProperty(this ref JsonPatch patch, Utf8JsonWriter writer,
            ReadOnlySpan<byte> jsonPath, ReadOnlySpan<byte> propertyName)
        {
            if (patch.TryGetJson(jsonPath, out ReadOnlyMemory<byte> received))
            {
                writer.WritePropertyName(propertyName);
#if NET6_0_OR_GREATER
                writer.WriteRawValue(received.Span);
#else
                using var document = JsonDocument.Parse(received);
                document.RootElement.WriteTo(writer);
#endif
            }
        }

        /// <summary>
        /// Comparison only: overlays live JSON on the effective patch value and mutates the patch.
        /// </summary>
        /// <remarks>
        /// This is NOT JsonPatch operation replay. It can restore deleted modeled fields, discard
        /// raw array edits, and override an explicit replacement or null. It is never the default.
        /// </remarks>
        internal static void WriteComposedModelWithLiveOverlay<T>(
            this ref JsonPatch patch,
            Utf8JsonWriter writer,
            ReadOnlySpan<byte> jsonPath,
            ReadOnlySpan<byte> propertyName,
            T value,
            ModelReaderWriterOptions options)
            where T : class, IJsonModel<T>
        {
            if (!patch.Contains(jsonPath))
            {
                // No competing patch entry: write directly.
                if (value is not null)
                {
                    writer.WritePropertyName(propertyName);
                    value.Write(writer, options);
                    return;
                }

                // Received but never read through the extension property. Nothing materialized it,
                // so emit the bytes exactly as they arrived; without this the value is dropped.
                if (!patch.IsRemoved(jsonPath)
                    && patch.TryGetJson(jsonPath, out ReadOnlyMemory<byte> received)
                    && received.Length > 0)
                {
                    writer.WritePropertyName(propertyName);
#if NET6_0_OR_GREATER
                    writer.WriteRawValue(received.Span);
#else
                    using var document = JsonDocument.Parse(received);
                    document.RootElement.WriteTo(writer);
#endif
                }

                return;
            }

            if (value is null || patch.IsRemoved(jsonPath))
            {
                return;
            }

            patch.TryGetJson(jsonPath, out ReadOnlyMemory<byte> raw);

            // Written through an ArrayBufferWriter and handed to Set as a span: a MemoryStream plus
            // ToArray would copy the merged bytes twice more on a path that runs on every write.
#if NETSTANDARD2_0
            using var merged = new MemoryStream();
#else
            var merged = new ArrayBufferWriter<byte>(raw.Length * 2);
#endif
            using (var mergeWriter = new Utf8JsonWriter(merged))
            {
                using var rawDocument = JsonDocument.Parse(raw);
                using var liveDocument = JsonDocument.Parse(
                    ModelReaderWriter.Write(value, options, SampleTypeSpecContext.Default));

                WriteMerged(mergeWriter, rawDocument.RootElement, liveDocument.RootElement);
            }

#if NETSTANDARD2_0
            patch.Set(jsonPath, merged.ToArray().AsSpan());
#else
            patch.Set(jsonPath, merged.WrittenSpan);
#endif
        }

        /// <summary> Deep-merges <paramref name="overlay"/> over <paramref name="baseElement"/>. </summary>
        private static void WriteMerged(Utf8JsonWriter writer, JsonElement baseElement, JsonElement overlay)
        {
            // Only objects merge. Anything else is replaced outright, so a raw scalar written where
            // the model has an object (or vice versa) does not produce a hybrid.
            if (baseElement.ValueKind != JsonValueKind.Object || overlay.ValueKind != JsonValueKind.Object)
            {
                overlay.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();

            foreach (JsonProperty property in baseElement.EnumerateObject())
            {
                if (overlay.TryGetProperty(property.Name, out JsonElement overlayValue))
                {
                    writer.WritePropertyName(property.Name);
                    WriteMerged(writer, property.Value, overlayValue);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            foreach (JsonProperty property in overlay.EnumerateObject())
            {
                if (!baseElement.TryGetProperty(property.Name, out _))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }
    }
}
