// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// PATTERN 3 of 3: composes target with INPUT + OUTPUT (round-trip) usage.
//
// MyShark's TCGC usage is `Input,Output,Json`. Simulates:
//
//   @clientOption("composes", MyShark, "csharp")
//   model MySharkOptions {
//     tag: string;
//     tracker: TrackerReference;
//   }
//
// Shape: get + set, including the model-typed property -- which is the contrast with MyLizard,
// where the same TrackerReference property is get-only.
//
// This is the least surprising of the three: MyShark's own Name and FinCount are already
// `{ get; set; }`, so composed properties match the shape of the type they attach to. A round-trip
// model is returned by the service and then modified and sent back, which is exactly the usage
// pattern composed properties need to support.

#nullable disable

using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;

namespace SampleTypeSpec
{
    /// <summary> Extension properties contributed to <see cref="MyShark"/>. </summary>
    public static partial class MySharkExtensions
    {
        extension(MyShark shark)
        {
            /// <summary> Gets or sets the Tag. </summary>
            [Experimental("SCME0001")]
            public string Tag
            {
                get => shark.Patch.GetStringOrDefault("$.tag"u8);
                set => shark.Patch.SetOrRemove("$.tag"u8, value);
            }

            /// <summary> Gets or sets the Tracker. </summary>
            [Experimental("SCME0001")]
            public TrackerReference Tracker
            {
                get => shark.Patch.GetJsonModelOrDefault<TrackerReference>("$.tracker"u8);
                set => shark.Patch.SetJsonModelOrRemove("$.tracker"u8, value);
            }
        }
    }
}
