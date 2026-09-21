// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using System;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SampleTypeSpec
{
#pragma warning disable SCME0001

    /// <summary> Ordinary composed properties for the two mixed dynamic targets. </summary>
    public static class MixedDynamicModelExtensions
    {
        static MixedDynamicModelExtensions()
        {
            DynamicModel.AddComposedWriter(
                static (writer, target, options) => MixedComposedState<DynamicModel>.Write(writer, target, ref target.Patch, options), "tracker", "profile");
            NullableDynamicModel.AddComposedWriter(
                static (writer, target, options) => MixedComposedState<NullableDynamicModel>.Write(writer, target, ref target.Patch, options), "tracker", "profile");
        }

        extension(DynamicModel target)
        {
            /// <summary> Gets or sets the ordinary composed tracker. </summary>
            [Experimental("SCME0001")]
            public TrackerReference Tracker
            {
                get => MixedComposedState<DynamicModel>.Table.GetOrCreateValue(target).Tracker.GetValue(ref target.Patch, "$.tracker"u8);
                set => MixedComposedState<DynamicModel>.Table.GetOrCreateValue(target).Tracker.Value = value;
            }

            /// <summary> Gets or sets the ordinary composed graph. </summary>
            [Experimental("SCME0001")]
            public RoundTripModel Profile
            {
                get => MixedComposedState<DynamicModel>.Table.GetOrCreateValue(target).Profile.GetValue(ref target.Patch, "$.profile"u8);
                set => MixedComposedState<DynamicModel>.Table.GetOrCreateValue(target).Profile.Value = value;
            }
        }

        extension(NullableDynamicModel target)
        {
            /// <summary> Gets or sets the ordinary composed tracker. </summary>
            [Experimental("SCME0001")]
            public TrackerReference Tracker
            {
                get => MixedComposedState<NullableDynamicModel>.Table.GetOrCreateValue(target).Tracker.GetValue(ref target.Patch, "$.tracker"u8);
                set => MixedComposedState<NullableDynamicModel>.Table.GetOrCreateValue(target).Tracker.Value = value;
            }

            /// <summary> Gets or sets the ordinary composed graph. </summary>
            [Experimental("SCME0001")]
            public RoundTripModel Profile
            {
                get => MixedComposedState<NullableDynamicModel>.Table.GetOrCreateValue(target).Profile.GetValue(ref target.Patch, "$.profile"u8);
                set => MixedComposedState<NullableDynamicModel>.Table.GetOrCreateValue(target).Profile.Value = value;
            }
        }
    }
#pragma warning restore SCME0001
}
