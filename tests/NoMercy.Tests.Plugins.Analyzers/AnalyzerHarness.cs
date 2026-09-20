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
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NoMercy.Tests.Plugins.Analyzers;

/// <summary>
/// Compiles a snippet and runs one analyzer over it.
/// <para>
/// It asserts the snippet itself compiles clean first. An analyzer over code
/// that does not compile reports whatever the broken semantic model happens to
/// resolve, which is usually nothing, and the test then passes by finding the
/// diagnostic count it expected for the wrong reason.
/// </para>
/// </summary>
public static class AnalyzerHarness
{
    public static async Task<IReadOnlyList<Diagnostic>> RunAsync<TAnalyzer>(string source)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        IEnumerable<PortableExecutableReference> references = AppDomain
            .CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Snippet",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new(OutputKind.DynamicallyLinkedLibrary)
        );

        ImmutableArray<Diagnostic> compileErrors =
        [
            .. compilation
                .GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
        ];

        if (compileErrors.Length > 0)
            throw new InvalidOperationException(
                "The snippet does not compile, so any analyzer result would be meaningless: "
                    + string.Join("; ", compileErrors.Select(error => error.GetMessage()))
            );

        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers([new TAnalyzer()]);

        return [.. await withAnalyzers.GetAnalyzerDiagnosticsAsync()];
    }
}
