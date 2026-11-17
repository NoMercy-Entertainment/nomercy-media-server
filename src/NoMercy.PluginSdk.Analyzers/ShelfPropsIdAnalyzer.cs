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
/// NMP0017: an app shelf or card written by hand without its id in props.
/// <para>
/// The app's grid, music card, row and page read the id from their props as
/// well as from the envelope. Without it the whole shelf fails to decode, and
/// the screen draws nothing and says nothing. This catches it at build time,
/// where the fix is one line, instead of as an empty screen.
/// </para>
/// <para>
/// It only reports what it can prove. Props built somewhere else, from a
/// variable or a method, are not read, and neither is a <c>Design</c> record,
/// which is merged into the props under names this rule cannot see. A plugin
/// that builds its props either way is never flagged by mistake.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ShelfPropsIdAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "NMP0017";

    private const string ComponentType = "NoMercy.PluginSdk.Abstractions.PluginComponent";

    private const string IdKey = "id";

    /// <summary>
    /// The app components that need the id on their props. The names the
    /// server's own home page sends, and that PluginShelves writes.
    /// </summary>
    public static IReadOnlyCollection<string> Components { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NMGrid",
            "NMMusicCard",
            "NMCarousel",
            "NMLolomo",
        };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(PluginDiagnostics.For(RuleId));

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Inspect,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression
        );
    }

    private static void Inspect(SyntaxNodeAnalysisContext context)
    {
        BaseObjectCreationExpressionSyntax creation = (BaseObjectCreationExpressionSyntax)
            context.Node;

        if (creation.Initializer is not { } initializer)
            return;

        if (
            context.SemanticModel.GetTypeInfo(creation).Type is not INamedTypeSymbol type
            || type.ToDisplayString() != ComponentType
        )
            return;

        string? component = null;
        ExpressionSyntax? props = null;
        bool hasDesign = false;

        foreach (ExpressionSyntax expression in initializer.Expressions)
        {
            if (
                expression
                is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax name } assignment
            )
                continue;

            if (name.Identifier.ValueText == "Component")
                component =
                    context.SemanticModel.GetConstantValue(assignment.Right).Value as string;
            else if (name.Identifier.ValueText == "Props")
                props = assignment.Right;
            else if (name.Identifier.ValueText == "Design")
                hasDesign = true;
        }

        if (component is null || !Components.Contains(component))
            return;

        // Design is merged into the props on the wire, under the names its own
        // [JsonProperty] attributes give. Whether one of them is "id" is not
        // something this rule can see, so a node with a Design is never reported.
        if (hasDesign)
            return;

        if (props is not null && !ProvablyMissesId(context.SemanticModel, props))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(PluginDiagnostics.For(RuleId), creation.GetLocation())
        );
    }

    /// <summary>
    /// True only for a dictionary written right there whose keys are all known
    /// and none of them is "id". Anything else may well carry it.
    /// </summary>
    private static bool ProvablyMissesId(SemanticModel model, ExpressionSyntax props)
    {
        if (props is not BaseObjectCreationExpressionSyntax created)
            return false;

        // new(other) copies a dictionary this rule cannot see into.
        if (created.ArgumentList is { Arguments.Count: > 0 })
            return false;

        if (created.Initializer is null)
            return true;

        foreach (ExpressionSyntax entry in created.Initializer.Expressions)
        {
            ExpressionSyntax? key = entry switch
            {
                // ["id"] = value
                AssignmentExpressionSyntax
                {
                    Left: ImplicitElementAccessSyntax { ArgumentList.Arguments.Count: 1 } access,
                } => access.ArgumentList.Arguments[0].Expression,
                // { "id", value }
                InitializerExpressionSyntax { Expressions.Count: 2 } pair => pair.Expressions[0],
                _ => null,
            };

            if (key is null)
                return false;

            object? value = model.GetConstantValue(key).Value;
            if (value is not string text || text == IdKey)
                return false;
        }

        return true;
    }
}
