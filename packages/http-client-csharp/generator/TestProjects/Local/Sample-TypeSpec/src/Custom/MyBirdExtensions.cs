// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

// PATTERN 1 of 3: composes target with INPUT usage.
//
// MyBird's TCGC usage is `Input,Json`. Simulates:
//
//   @clientOption("composes", MyBird, "csharp")
//   model MyBirdOptions {
//     nickname: string;
//     wingspan?: float32;
//   }
//
// Shape: get + set.
//
// Note the deliberate divergence from PropertyProvider.PropertyHasSetter. For an input-only model
// that helper returns FALSE for required properties, because they are assigned through the
// constructor -- which is why MyBird's own Color and Age are get-only. Composed properties have no
// constructor hook, so mirroring that rule would leave them permanently unsettable. The presence of
// the Input flag, not the target's own property style, is what drives the setter here.

#nullable disable

using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;

namespace SampleTypeSpec
{
    /// <summary> Extension properties contributed to <see cref="MyBird"/>. </summary>
    public static partial class MyBirdExtensions
    {
        extension(MyBird bird)
        {
            /// <summary> Gets or sets the Nickname. </summary>
            [Experimental("SCME0001")]
            public string Nickname
            {
                get => bird.Patch.GetStringOrDefault("$.nickname"u8);
                set => bird.Patch.SetOrRemove("$.nickname"u8, value);
            }

            /// <summary> Gets or sets the Wingspan. </summary>
            [Experimental("SCME0001")]
            public float? Wingspan
            {
                get => bird.Patch.GetNullableSingleOrDefault("$.wingspan"u8);
                set => bird.Patch.SetOrRemove("$.wingspan"u8, value);
            }
        }
    }
}
