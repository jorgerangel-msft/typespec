// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#if !NET8_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class ExperimentalAttribute : Attribute
    {
        public ExperimentalAttribute(string diagnosticId) => DiagnosticId = diagnosticId;

        public string DiagnosticId { get; }
        public string? UrlFormat { get; set; }
    }
}
#endif
