// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001
    internal struct ComposedListValue<T>
    {
        private IList<T> _value;
        private bool _initialized;
        private bool _assigned;
        private bool _receivedNull;

        public IList<T> Value
        {
            get => _value;
            set
            {
                _value = value;
                _initialized = true;
                _assigned = true;
            }
        }

        public IList<T> GetValue(ref JsonPatch patch, ReadOnlySpan<byte> path, Func<JsonElement, T> readElement)
        {
            if (!_initialized)
            {
                if (!patch.IsRemoved(path) && patch.TryGetJson(path, out ReadOnlyMemory<byte> json) && !json.IsEmpty)
                {
                    using var document = JsonDocument.Parse(json, ModelSerializationExtensions.JsonDocumentOptions);
                    if (document.RootElement.ValueKind == JsonValueKind.Null)
                    {
                        _receivedNull = true;
                    }
                    else
                    {
                        var items = new List<T>(document.RootElement.GetArrayLength());
                        foreach (var item in document.RootElement.EnumerateArray())
                        {
                            items.Add(readElement(item));
                        }
                        _value = items;
                    }
                }
                _initialized = true;
            }
            return _value;
        }

        public readonly void Write(Utf8JsonWriter writer, ref JsonPatch patch, ReadOnlySpan<byte> path,
            ReadOnlySpan<byte> propertyName, Action<Utf8JsonWriter, T> writeElement)
        {
            if (!_initialized)
            {
                patch.WriteRawProperty(writer, path, propertyName);
            }
            else if (_value is not null)
            {
                writer.WritePropertyName(propertyName);
                writer.WriteStartArray();
                for (int i = 0; i < _value.Count; i++)
                {
                    writeElement(writer, _value[i]);
                }
                writer.WriteEndArray();
            }
            else if (_receivedNull && !_assigned)
            {
                writer.WriteNull(propertyName);
            }
        }
    }
#pragma warning restore SCME0001
}
