// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001
    internal struct ComposedModelValue<T> where T : class, IJsonModel<T>
    {
        private T _value;
        private bool _initialized;
        private bool _assigned;
        private bool _receivedNull;

        public T Value
        {
            get => _value;
            set
            {
                _value = value;
                _initialized = true;
                _assigned = true;
            }
        }

        public T GetValue(ref JsonPatch patch, ReadOnlySpan<byte> path)
        {
            if (!_initialized)
            {
                _value = patch.GetJsonModelOrDefault<T>(path, out bool hasValue);
                _receivedNull = hasValue && _value is null;
                _initialized = true;
            }
            return _value;
        }

        public void Write(Utf8JsonWriter writer, ref JsonPatch patch, ReadOnlySpan<byte> path, ReadOnlySpan<byte> propertyName,
            ModelReaderWriterOptions options)
        {
            // Registration alone does not materialize or normalize received JSON.
            if (!_initialized)
            {
                patch.WriteRawProperty(writer, path, propertyName);
            }
            else if (_value is not null)
            {
                // Getter-only materialization still retains live nested mutations.
                ComposedPatchHelpers.WriteJsonModel(writer, propertyName, _value, ModelReaderWriterOptions.Json);
            }
            else if (_receivedNull && !_assigned)
            {
                writer.WriteNull(propertyName);
            }
        }
    }
#pragma warning restore SCME0001
}
