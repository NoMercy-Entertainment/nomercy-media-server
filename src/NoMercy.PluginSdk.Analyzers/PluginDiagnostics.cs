// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NoMercy.PluginSdk.Analyzers;

/// <summary>
/// One descriptor per rule, built from the generated table.
/// <para>
/// Built rather than written, so the sentence an author reads in the editor is
/// the sentence the refusal gives them at run time. Two hand-written copies
/// drift, and the one that drifts is always the one they read first.
/// </para>
/// </summary>
public static class PluginDiagnostics
{
    public const string Category = "NoMercy.Plugins";

    private static readonly ImmutableDictionary<string, DiagnosticDescriptor> Descriptors =
        PluginAnalyzerDescriptors.All.ToImmutableDictionary(
            descriptor => descriptor.Id,
            descriptor => new DiagnosticDescriptor(
                descriptor.Id,
                descriptor.Title,
                descriptor.Title,
                Category,
                DiagnosticSeverity.Warning,
                isEnabledByDefault: true
            )
        );

    public static DiagnosticDescriptor For(string id) => Descriptors[id];

    public static ImmutableArray<DiagnosticDescriptor> All => Descriptors.Values.ToImmutableArray();
}
