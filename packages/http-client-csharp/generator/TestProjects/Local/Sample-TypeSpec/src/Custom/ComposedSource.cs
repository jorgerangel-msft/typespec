// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001

    internal static class ComposedSource
    {
        internal static bool ApplyEdit(ref JsonPatch patch, ref bool publishing,
            ReadOnlySpan<byte> jsonPath, JsonPatch.EncodedValue value)
        {
            if (publishing || jsonPath.SequenceEqual("$"u8))
            {
                return false;
            }
            string name = GetRootPropertyName(jsonPath, out int rootLength);
#if !COMPOSES_JSONPATH_BUGFIX
            if (name.IndexOfAny(new[] { '\'', '"', '\\' }) >= 0 || jsonPath.Slice(0, rootLength).IndexOf((byte)'\\') >= 0)
            {
                throw new NotSupportedException("System.ClientModel 1.15 cannot reliably address this escaped received root property.");
            }
#endif
            ReadOnlySpan<byte> rootPath = jsonPath.Slice(0, rootLength);
            if (patch.IsRemoved(rootPath))
            {
                return false;
            }
            if (!patch.TryGetJson(rootPath, out ReadOnlyMemory<byte> current))
            {
                return false;
            }

            // Project the explicit raw edit only, then publish after successful serialization.
            var scoped = new JsonPatch(current);
            scoped.Set([.. "$"u8, .. jsonPath.Slice(rootLength)], value);
            if (rootLength == jsonPath.Length && scoped.Contains("$"u8))
            {
                return false;
            }
            BinaryData replacement = scoped.ToBinaryData("J");
            // Publishing reenters the installed setter; do not project our own whole-member Set.
            publishing = true;
            try
            {
                patch.Set(rootPath, replacement);
            }
            finally
            {
                publishing = false;
            }
            return true;
        }

        internal static void WriteUnclaimed(Utf8JsonWriter writer, ref JsonPatch patch,
            ReadOnlyMemory<byte> originalInput, IDictionary<string, BinaryData> source,
            ISet<string> propertyNames, ISet<string> declaredPropertyNames, string[] additionalDeclaredNames = null)
        {
            // Enumerate immutable received bytes, not a projection of the mutable patch.
            if (!originalInput.IsEmpty)
            {
                using var document = JsonDocument.Parse(originalInput, ModelSerializationExtensions.JsonDocumentOptions);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (ShouldWrite(property.Name, ref patch, propertyNames, declaredPropertyNames, additionalDeclaredNames))
                    {
                        property.WriteTo(writer);
                    }
                }
                return;
            }

            // Full-constructor/factory data without a received payload keeps its existing source path.
            if (source is null)
            {
                return;
            }
            foreach (var property in source)
            {
                if (ShouldWrite(property.Key, ref patch, propertyNames, declaredPropertyNames, additionalDeclaredNames))
                {
                    writer.WritePropertyName(property.Key);
#if NET6_0_OR_GREATER
                    writer.WriteRawValue(property.Value);
#else
                    using var document = JsonDocument.Parse(property.Value);
                    document.RootElement.WriteTo(writer);
#endif
                }
            }
        }

        private static bool ShouldWrite(string name, ref JsonPatch patch, ISet<string> propertyNames,
            ISet<string> declaredPropertyNames, string[] additionalDeclaredNames)
        {
            if (declaredPropertyNames.Contains(name) || propertyNames.Contains(name)
                || additionalDeclaredNames is not null && Array.IndexOf(additionalDeclaredNames, name) >= 0)
            {
                return false;
            }
            byte[] path = Encoding.UTF8.GetBytes("$[" + JsonSerializer.Serialize(name) + "]");
            return !patch.Contains("$"u8, Encoding.UTF8.GetBytes(name)) && !patch.IsRemoved(path);
        }

        internal static string GetRootPropertyName(ReadOnlySpan<byte> jsonPath, out int rootLength)
        {
            if (jsonPath.Length < 3 || jsonPath[0] != '$')
            {
                throw new ArgumentException("Expected a JSON path beginning with a root property selector.", nameof(jsonPath));
            }
            if (jsonPath[1] == '.')
            {
                rootLength = 2;
                while (rootLength < jsonPath.Length && jsonPath[rootLength] != '.' && jsonPath[rootLength] != '[')
                {
                    rootLength++;
                }
                if (rootLength == 2)
                {
                    throw new ArgumentException("The root property selector must not be empty.", nameof(jsonPath));
                }
                return Encoding.UTF8.GetString(jsonPath.Slice(2, rootLength - 2).ToArray());
            }
            if (jsonPath[1] != '[' || (jsonPath[2] != '\'' && jsonPath[2] != '"'))
            {
                throw new ArgumentException("Expected a dot or quoted root property selector.", nameof(jsonPath));
            }
            byte quote = jsonPath[2];
            int end = 3;
            while (end < jsonPath.Length)
            {
                if (jsonPath[end] == '\\')
                    end += 2;
                else if (jsonPath[end] == quote)
                    break;
                else
                    end++;
            }
            if (end >= jsonPath.Length - 1 || jsonPath[end + 1] != ']')
            {
                throw new ArgumentException("The quoted root property selector is not terminated.", nameof(jsonPath));
            }
            rootLength = end + 2;
            if (quote == '"')
            {
                return JsonSerializer.Deserialize<string>(jsonPath.Slice(2, end - 1));
            }
            string encoded = Encoding.UTF8.GetString(jsonPath.Slice(3, end - 3).ToArray());
            var jsonName = new StringBuilder("\"");
            for (int i = 0; i < encoded.Length; i++)
            {
                char current = encoded[i];
                if (current == '\\' && i + 1 < encoded.Length)
                {
                    char escaped = encoded[++i];
                    if (escaped != '\'')
                        jsonName.Append('\\');
                    jsonName.Append(escaped);
                }
                else
                {
                    if (current == '"')
                        jsonName.Append('\\');
                    jsonName.Append(current);
                }
            }
            jsonName.Append('"');
            return JsonSerializer.Deserialize<string>(jsonName.ToString());
        }
    }

#pragma warning restore SCME0001
}
