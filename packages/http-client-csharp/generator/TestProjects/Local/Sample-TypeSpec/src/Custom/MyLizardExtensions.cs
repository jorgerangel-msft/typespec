// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// PATTERN 2 of 3: composes target with OUTPUT usage.
//
// MyLizard's TCGC usage is `Output,Json`. Simulates:
//
//   @clientOption("composes", MyLizard, "csharp")
//   model MyLizardOptions {
//     habitat: string;
//     tracker: TrackerReference;
//   }
//
// Shape: get only, expression-bodied, reading straight from the patch.
//
// This matches how the generator already treats output-only models -- MyLizard's own Species and
// LengthInches are get-only with an internal constructor, because a response type is not something
// the caller is expected to mutate. Emitting setters here would let `composes` quietly make an
// immutable response model mutable, which is the one thing this pattern exists to prevent.
//
// A side benefit: the reference-identity limitation of patch-backed model properties disappears
// here. The getter returns a fresh instance per access, but because the returned model is not
// something the caller is expected to mutate, snapshot semantics are unobservable.

#nullable disable

using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;

namespace SampleTypeSpec
{
    /// <summary> Extension properties contributed to <see cref="MyLizard"/>. </summary>
    public static partial class MyLizardExtensions
    {
        extension(MyLizard lizard)
        {
            /// <summary> Gets the Habitat. </summary>
            [Experimental("SCME0001")]
            public string Habitat => lizard.Patch.GetStringOrDefault("$.habitat"u8);

            /// <summary> Gets the Tracker. </summary>
            [Experimental("SCME0001")]
            public TrackerReference Tracker => lizard.Patch.GetJsonModelOrDefault<TrackerReference>("$.tracker"u8);
        }
    }
}
