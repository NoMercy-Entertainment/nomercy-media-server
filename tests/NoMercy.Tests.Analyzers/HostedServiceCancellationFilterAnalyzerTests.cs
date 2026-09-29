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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NoMercy.Analyzers;
using Xunit;

namespace NoMercy.Tests.Analyzers;

public sealed class HostedServiceCancellationFilterAnalyzerTests
{
    private const string HostingStub = """
        namespace Microsoft.Extensions.Hosting
        {
            public interface IHostedService { }
            public abstract class BackgroundService : IHostedService { }
        }
        """;

    [Fact]
    public async Task PluginTelemetryServiceTimeoutFilterIsReported()
    {
        string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;

            public sealed class PluginTelemetryService : BackgroundService
            {
                public async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    using PeriodicTimer timer = new(TimeSpan.FromHours(1));
                    while (await timer.WaitForNextTickAsync(stoppingToken))
                    {
                        try
                        {
                            await SendAsync(stoppingToken);
                        }
                        catch (Exception exception) when ({|#0:exception is not OperationCanceledException|})
                        {
                        }
                    }
                }

                private static Task SendAsync(CancellationToken token) => Task.Delay(1, token);
            }
            """;

        CSharpAnalyzerTest<HostedServiceCancellationFilterAnalyzer, DefaultVerifier> test = new()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.TestState.Sources.Add(("PluginTelemetryService.cs", source));
        test.TestState.Sources.Add(("HostingStub.cs", HostingStub));
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(
                HostedServiceCancellationFilterAnalyzer.DiagnosticId,
                DiagnosticSeverity.Error
            ).WithLocation(0)
        );

        await test.RunAsync();
    }

    [Theory]
    [InlineData("OperationCanceledException", true, true)]
    [InlineData("OperationCanceledException", true, false)]
    [InlineData("OperationCanceledException", false, false)]
    [InlineData("TaskCanceledException", true, false)]
    public async Task FilterRequiresTokenCheckOnlyInHostedService(
        string excludedException,
        bool hosted,
        bool tokenCheck
    )
    {
        string baseType = hosted ? "BackgroundService" : "object";
        string filter = $"ex is not {excludedException}";
        if (tokenCheck)
            filter += " || !stoppingToken.IsCancellationRequested";

        string markedFilter = hosted && !tokenCheck ? $"{{|#0:{filter}|}}" : filter;
        string source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;

            public sealed class Service : {{baseType}}
            {
                public async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    try
                    {
                        await Task.Delay(1, stoppingToken);
                    }
                    catch (Exception ex) when ({{markedFilter}})
                    {
                    }
                }
            }
            """;

        CSharpAnalyzerTest<HostedServiceCancellationFilterAnalyzer, DefaultVerifier> test = new()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.TestState.Sources.Add(("Service.cs", source));
        test.TestState.Sources.Add(("HostingStub.cs", HostingStub));
        if (hosted && !tokenCheck)
        {
            test.ExpectedDiagnostics.Add(
                new DiagnosticResult(
                    HostedServiceCancellationFilterAnalyzer.DiagnosticId,
                    DiagnosticSeverity.Error
                ).WithLocation(0)
            );
        }

        await test.RunAsync();
    }
}
