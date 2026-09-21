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
    // Hand-written generated-shape proof; the emitter frontend does not generate these members.
    public partial class DynamicModel
    {
        private readonly IDictionary<string, BinaryData> _additionalBinaryDataProperties;
        private readonly ReadOnlyMemory<byte> _originalInput;
        private static readonly HashSet<string> DeclaredPropertyNames = new(StringComparer.Ordinal)
        {
            "name", "optionalUnknown", "optionalInt", "optionalNullableList", "requiredNullableList",
            "optionalNullableDictionary", "requiredNullableDictionary", "primitiveDictionary", "foo",
            "listFoo", "listOfListFoo", "dictionaryFoo", "dictionaryOfDictionaryFoo", "dictionaryListFoo", "listOfDictionaryFoo",
        };
        private bool _publishingComposedEdit;
        private static readonly ComposedWriterRegistry<DynamicModel> ComposedWriters = new();

        /// <summary> Registers a writer and the source names it owns at serialization. </summary>
        /// <param name="writer"> Composed-property writer. </param>
        /// <param name="serializedPropertyNames"> Serialized property names; never consulted during reading. </param>
        /// <remarks> Client authors are responsible for avoiding duplicate property names. </remarks>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Experimental("SCME0001")]
        public static void AddComposedWriter(Action<Utf8JsonWriter, DynamicModel, ModelReaderWriterOptions> writer, params string[] serializedPropertyNames)
            => ComposedWriters.Add(writer, serializedPropertyNames);
    }
}
