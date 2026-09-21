// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001
    public partial class NullableDynamicModel
    {
        private readonly IDictionary<string, BinaryData> _additionalBinaryDataProperties;
        private readonly ReadOnlyMemory<byte> _originalInput;
        private readonly string[] _additionalDeclaredPropertyNames;
        private static readonly HashSet<string> DeclaredPropertyNames = new(StringComparer.Ordinal)
        {
            "modelValue", "children", "childDictionary", "nestedChildren", "nestedChildDictionary", "dictionaryChildren", "listOfDictionaries",
        };
        private bool _publishingComposedEdit;
        private static readonly ComposedWriterRegistry<NullableDynamicModel> ComposedWriters = new();

        /// <summary> Registers a writer and the source names it owns at serialization. </summary>
        /// <param name="writer"> Composed-property writer. </param>
        /// <param name="serializedPropertyNames"> Serialized property names; never consulted during reading. </param>
        /// <remarks> Client authors are responsible for avoiding duplicate property names. </remarks>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Experimental("SCME0001")]
        public static void AddComposedWriter(Action<Utf8JsonWriter, NullableDynamicModel, ModelReaderWriterOptions> writer, params string[] serializedPropertyNames)
            => ComposedWriters.Add(writer, serializedPropertyNames);

        // Internal prototype seams for a same-assembly derived model; not new protected SDK API.
        internal bool PropagateBaseSet(ReadOnlySpan<byte> path, JsonPatch.EncodedValue value)
            => PropagateSet(path, value);

        internal bool PropagateBaseGet(ReadOnlySpan<byte> path, out JsonPatch.EncodedValue value)
            => PropagateGet(path, out value);

        internal NullableDynamicModel(NullableDynamicModel source, string[] additionalDeclaredNames)
            : this(source.ModelValue, source.Children, source.ChildDictionary, source.NestedChildren,
                source.NestedChildDictionary, source.DictionaryChildren, source.ListOfDictionaries, source._patch,
                CopySourceWithoutDeclaredNames(source._additionalBinaryDataProperties, additionalDeclaredNames), source._originalInput)
        {
            _additionalDeclaredPropertyNames = additionalDeclaredNames;
        }

        private static IDictionary<string, BinaryData> CopySourceWithoutDeclaredNames(
            IDictionary<string, BinaryData> source, string[] declaredNames)
        {
            if (source is null)
                return null;
            var result = new Dictionary<string, BinaryData>(source, StringComparer.Ordinal);
            foreach (string name in declaredNames)
                result.Remove(name);
            return result;
        }
    }
#pragma warning restore SCME0001
}
