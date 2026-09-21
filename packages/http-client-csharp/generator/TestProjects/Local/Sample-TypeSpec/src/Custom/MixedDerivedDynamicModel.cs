// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001
    // Bounded same-assembly inheritance proof, not automatic frontend generation.
    internal sealed class MixedDerivedDynamicModel : NullableDynamicModel
    {
        private readonly string _derivedName;
        private readonly byte[] _derivedPath;

        public MixedDerivedDynamicModel(AnotherDynamicModel inheritedChild, AnotherDynamicModel derivedChild, string derivedName)
        {
            _derivedName = derivedName;
            _derivedPath = Encoding.UTF8.GetBytes("$." + derivedName);
            ModelValue = inheritedChild;
            DerivedChild = derivedChild;
            Patch.SetPropagators(PropagateSet, PropagateGet);
        }

        private MixedDerivedDynamicModel(NullableDynamicModel source, AnotherDynamicModel derivedChild)
            : base(source, new[] { "derivedChild" })
        {
            _derivedName = "derivedChild";
            _derivedPath = "$.derivedChild"u8.ToArray();
            DerivedChild = derivedChild;
            Patch.SetPropagators(PropagateSet, PropagateGet);
        }

        public AnotherDynamicModel DerivedChild { get; set; }

        protected override void JsonModelWriteCore(Utf8JsonWriter writer, ModelReaderWriterOptions options)
        {
            if (!Patch.Contains(_derivedPath) && DerivedChild is not null)
            {
                writer.WritePropertyName(_derivedName);
                ((IJsonModel<AnotherDynamicModel>)DerivedChild).Write(writer, options);
            }
            base.JsonModelWriteCore(writer, options);
        }

        protected override NullableDynamicModel PersistableModelCreateCore(BinaryData data, ModelReaderWriterOptions options)
        {
            using var document = JsonDocument.Parse(data);
            return Read(document.RootElement, data, options);
        }

        protected override NullableDynamicModel JsonModelCreateCore(ref Utf8JsonReader reader, ModelReaderWriterOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return Read(document.RootElement, null, options);
        }

        private static NullableDynamicModel Read(JsonElement element, BinaryData data, ModelReaderWriterOptions options)
        {
            var source = DeserializeNullableDynamicModel(element, data, options);
            if (source is null)
                return null;
            AnotherDynamicModel child = null;
            if (element.TryGetProperty("derivedChild", out var value))
            {
                child = AnotherDynamicModel.DeserializeAnotherDynamicModel(value, value.GetUtf8Bytes(), options);
            }
            return new MixedDerivedDynamicModel(source, child);
        }

        private bool PropagateSet(ReadOnlySpan<byte> path, JsonPatch.EncodedValue value)
        {
            ReadOnlySpan<byte> local = path.SliceToStartOfPropertyName();
            ReadOnlySpan<byte> name = _derivedPath.AsSpan(2);
            if (local.StartsWith(name))
            {
                if (DerivedChild is null)
                    return false;
                DerivedChild.Patch.Set([.. "$"u8, .. local.Slice(name.Length)], value);
                return true;
            }
            return base.PropagateBaseSet(path, value);
        }

        private bool PropagateGet(ReadOnlySpan<byte> path, out JsonPatch.EncodedValue value)
        {
            ReadOnlySpan<byte> local = path.SliceToStartOfPropertyName();
            ReadOnlySpan<byte> name = _derivedPath.AsSpan(2);
            if (local.StartsWith(name))
            {
                value = default;
                if (DerivedChild is null)
                    return false;
                return DerivedChild.Patch.TryGetEncodedValue([.. "$"u8, .. local.Slice(name.Length)], out value);
            }
            return base.PropagateBaseGet(path, out value);
        }
    }
#pragma warning restore SCME0001
}
