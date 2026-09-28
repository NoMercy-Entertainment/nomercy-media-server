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
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NoMercy.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
[SuppressMessage(category: "MicrosoftCodeAnalysisCorrectness", checkId: "RS1038")]
public sealed class HostedServiceCancellationFilterAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NMS003";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Keep timeouts inside hosted service catches",
        messageFormat: "Check a cancellation token's IsCancellationRequested in this catch filter",
        category: "NoMercy.Hosting",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
    {
        CatchClauseSyntax catchClause = (CatchClauseSyntax)context.Node;
        CatchFilterClauseSyntax? filter = catchClause.Filter;
        if (filter is null || catchClause.Declaration is null)
            return;

        INamedTypeSymbol? hostedService = context.Compilation.GetTypeByMetadataName(
            "Microsoft.Extensions.Hosting.IHostedService"
        );
        INamedTypeSymbol? containingType = context.ContainingSymbol?.ContainingType;
        if (hostedService is null || containingType is null)
            return;

        bool isHostedService = false;
        foreach (INamedTypeSymbol implementedInterface in containingType.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implementedInterface, hostedService))
            {
                isHostedService = true;
                break;
            }
        }

        if (!isHostedService)
            return;

        bool excludesCancellation = false;
        foreach (SyntaxNode node in filter.FilterExpression.DescendantNodesAndSelf())
        {
            if (node is not IsPatternExpressionSyntax patternExpression)
                continue;
            if (patternExpression.Expression is not IdentifierNameSyntax exceptionName)
                continue;
            if (
                !string.Equals(
                    exceptionName.Identifier.ValueText,
                    catchClause.Declaration.Identifier.ValueText,
                    StringComparison.Ordinal
                )
            )
                continue;
            if (patternExpression.Pattern is not UnaryPatternSyntax notPattern)
                continue;

            SyntaxNode? excludedType = notPattern.Pattern switch
            {
                TypePatternSyntax typePattern => typePattern.Type,
                DeclarationPatternSyntax declarationPattern => declarationPattern.Type,
                ConstantPatternSyntax constantPattern => constantPattern.Expression,
                _ => null,
            };
            if (excludedType is null)
                continue;

            ITypeSymbol? type =
                context.SemanticModel.GetSymbolInfo(excludedType).Symbol as ITypeSymbol;
            if (
                type?.ToDisplayString()
                is "System.OperationCanceledException"
                    or "System.Threading.Tasks.TaskCanceledException"
            )
            {
                excludesCancellation = true;
                break;
            }
        }

        if (!excludesCancellation || HasTokenCheck(filter.FilterExpression, context.SemanticModel))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, filter.FilterExpression.GetLocation()));
    }

    private static bool HasTokenCheck(ExpressionSyntax expression, SemanticModel model)
    {
        foreach (SyntaxNode node in expression.DescendantNodesAndSelf())
        {
            if (
                node is not BinaryExpressionSyntax binary
                || !binary.IsKind(SyntaxKind.LogicalOrExpression)
            )
                continue;

            if (
                IsNegatedCancellationCheck(binary.Left, model)
                || IsNegatedCancellationCheck(binary.Right, model)
            )
                return true;
        }

        return false;
    }

    private static bool IsNegatedCancellationCheck(ExpressionSyntax expression, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        if (
            expression is not PrefixUnaryExpressionSyntax negation
            || !negation.IsKind(SyntaxKind.LogicalNotExpression)
        )
            return false;

        ExpressionSyntax operand = negation.Operand;
        while (operand is ParenthesizedExpressionSyntax parenthesized)
            operand = parenthesized.Expression;

        if (operand is not MemberAccessExpressionSyntax member)
            return false;
        ISymbol? symbol = model.GetSymbolInfo(member).Symbol;
        return symbol is IPropertySymbol property
            && property.Name == "IsCancellationRequested"
            && property.ContainingType.ToDisplayString() == "System.Threading.CancellationToken";
    }
}
