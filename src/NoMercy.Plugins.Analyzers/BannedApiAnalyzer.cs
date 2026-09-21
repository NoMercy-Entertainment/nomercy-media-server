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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NoMercy.Plugins.Analyzers;

/// <summary>
/// Types a plugin must not construct itself.
/// <para>
/// Every one of these has a facade behind it that the owner granted. Reaching
/// the type directly is not a style question: it is how a plugin ends up
/// talking to a host the manifest never named, writing outside the folders it
/// was given, or holding a socket nothing can close when it stops.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BannedApiAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The banned type, by its full name, and the rule it raises.</summary>
    private static readonly IReadOnlyDictionary<string, string> Banned = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["System.Net.Http.HttpClient"] = "NMP0001",
        ["System.Net.Sockets.TcpClient"] = "NMP0002",
        ["System.Net.Sockets.Socket"] = "NMP0002",
        ["System.Net.Sockets.UdpClient"] = "NMP0002",
        ["System.Net.Sockets.TcpListener"] = "NMP0003",
        ["System.Net.HttpListener"] = "NMP0003",
        ["System.Diagnostics.Process"] = "NMP0004",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        Banned.Values.Distinct().Select(PluginDiagnostics.For).ToImmutableArray();

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        // Both creation kinds. `new HttpClient()` is an ObjectCreation and
        // `HttpClient client = new()` is an ImplicitObjectCreation, and the
        // second is the one an author actually writes. Registering only the
        // first left every real plugin unflagged, which is how the tests here
        // found it.
        context.RegisterSyntaxNodeAction(
            Inspect,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression
        );
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        ExpressionSyntax creation = (ExpressionSyntax)context.Node;

        if (context.SemanticModel.GetTypeInfo(creation).Type is not INamedTypeSymbol created)
            return;

        string name = created
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty);

        if (!Banned.TryGetValue(name, out string? id))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(PluginDiagnostics.For(id), creation.GetLocation())
        );
    }
}
