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

namespace NoMercy.PluginSdk.Analyzers;

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
        context.RegisterSyntaxNodeAction(
            Inspect,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression,
            SyntaxKind.InvocationExpression,
            SyntaxKind.SimpleMemberAccessExpression
        );
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        ExpressionSyntax expression = (ExpressionSyntax)context.Node;

        // The invocation owns a method access, so report it at the call site once.
        if (
            expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression == memberAccess
        )
            return;

        // A plain member read is not a creation: only creations and calls that hand back a
        // banned type count by their result type.
        string? id =
            expression is MemberAccessExpressionSyntax
                ? null
                : RuleFor(context.SemanticModel.GetTypeInfo(expression).Type as INamedTypeSymbol);

        // Static members of a banned type (Process.Start, HttpClient.DefaultProxy) are the
        // type's own entry points. Instance members of an object the plugin already holds
        // are left alone: the object was flagged where it was made.
        if (
            id is null
            && expression is InvocationExpressionSyntax or MemberAccessExpressionSyntax
            && context.SemanticModel.GetSymbolInfo(expression).Symbol is { IsStatic: true } symbol
        )
            id = RuleFor(symbol.ContainingType);

        if (id is null)
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(PluginDiagnostics.For(id), expression.GetLocation())
        );
    }

    private static string? RuleFor(INamedTypeSymbol? type)
    {
        if (type is null)
            return null;

        string name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty);

        return Banned.TryGetValue(name, out string? id) ? id : null;
    }
}
