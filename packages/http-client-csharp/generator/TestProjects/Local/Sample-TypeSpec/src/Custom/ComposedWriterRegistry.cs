// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001
    internal sealed class ComposedWriterRegistry<T>
    {
        private sealed class Snapshot
        {
            public readonly Action<Utf8JsonWriter, T, ModelReaderWriterOptions> Write;
            public readonly HashSet<string> PropertyNames;
            public Snapshot(Action<Utf8JsonWriter, T, ModelReaderWriterOptions> write,
                HashSet<string> propertyNames)
            {
                Write = write;
                PropertyNames = propertyNames;
            }
        }

        private Snapshot _snapshot = new Snapshot(null,
            new HashSet<string>(StringComparer.Ordinal));

        public void Add(Action<Utf8JsonWriter, T, ModelReaderWriterOptions> writer, string[] serializedPropertyNames)
        {
            Argument.AssertNotNull(writer, nameof(writer));
            Argument.AssertNotNull(serializedPropertyNames, nameof(serializedPropertyNames));
            foreach (string name in serializedPropertyNames)
            {
                Argument.AssertNotNull(name, nameof(serializedPropertyNames));
            }
            Snapshot current;
            Snapshot updated;
            do
            {
                current = Volatile.Read(ref _snapshot);
                var write = current.Write;
                var names = new HashSet<string>(current.PropertyNames, StringComparer.Ordinal);
                bool changed = false;
                foreach (string name in serializedPropertyNames)
                {
                    changed |= names.Add(name);
                }
                foreach (Action<Utf8JsonWriter, T, ModelReaderWriterOptions> callback in writer.GetInvocationList())
                {
                    if (write is null || Array.IndexOf(write.GetInvocationList(), callback) < 0)
                    {
                        write = (Action<Utf8JsonWriter, T, ModelReaderWriterOptions>)Delegate.Combine(write, callback);
                        changed = true;
                    }
                }
                if (!changed)
                    return;
                updated = new Snapshot(write, names);
            }
            while (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current));
        }

        public void Write(Utf8JsonWriter writer, T target, ModelReaderWriterOptions options,
            ref JsonPatch patch, ReadOnlyMemory<byte> originalInput, IDictionary<string, BinaryData> source,
            ISet<string> declaredPropertyNames, string[] additionalDeclaredNames = null)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            snapshot.Write?.Invoke(writer, target, options);
            ComposedSource.WriteUnclaimed(writer, ref patch, originalInput, source, snapshot.PropertyNames,
                declaredPropertyNames, additionalDeclaredNames);
        }
    }
#pragma warning restore SCME0001
}
