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

using FluentAssertions;
using Microsoft.CodeAnalysis;
using NoMercy.PluginSdk.Analyzers;
using Xunit;

namespace NoMercy.Tests.Plugins.Analyzers;

public class BannedApiAnalyzerTests
{
    [Fact]
    public async Task A_raw_TcpClient_is_flagged_with_NMP0002()
    {
        const string source = """
            using System.Net.Sockets;

            public class Peer
            {
                public void Connect()
                {
                    TcpClient client = new("tracker.example", 443);
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("NMP0002");
        diagnostics[0].GetMessage().Should().Contain("context.Net.DialAsync");
    }

    [Fact]
    public async Task A_bare_HttpClient_is_flagged_with_NMP0001()
    {
        const string source = """
            using System.Net.Http;

            public class Catalog
            {
                public void Fetch()
                {
                    HttpClient client = new();
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("NMP0001");
        diagnostics[0].GetMessage().Should().Contain("context.HttpClient");
    }

    [Fact]
    public async Task Starting_a_process_is_flagged_with_NMP0004()
    {
        const string source = """
            using System.Diagnostics;

            public class Tools
            {
                public void Run()
                {
                    Process process = new();
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("NMP0004");
        diagnostics[0].GetMessage().Should().Contain("context.Process.SpawnAsync");
    }

    [Fact]
    public async Task Starting_a_process_from_a_static_factory_is_flagged_once()
    {
        const string source = """
            using System.Diagnostics;

            public class Tools
            {
                public void Run()
                {
                    Process.Start("cmd");
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0004");
        diagnostics[0].GetMessage().Should().Contain("context.Process.SpawnAsync");
    }

    [Fact]
    public async Task Accepting_a_socket_from_a_factory_is_flagged_once()
    {
        const string source = """
            using System.Net.Sockets;

            public class Peer
            {
                public void Accept(Socket listener)
                {
                    Socket accepted = listener.Accept();
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0002");
    }

    [Fact]
    public async Task Accessing_a_static_member_of_a_banned_type_is_flagged_once()
    {
        const string source = """
            using System.Diagnostics;

            public class Tools
            {
                public void Inspect()
                {
                    int count = Process.GetProcesses().Length;
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0004");
    }

    [Fact]
    public async Task Accessing_a_static_property_of_a_banned_type_is_flagged_once()
    {
        const string source = """
            using System.Net.Http;

            public class Catalog
            {
                public object Proxy() => HttpClient.DefaultProxy;
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0001");
    }

    [Fact]
    public async Task Factory_on_an_unbanned_type_returning_a_banned_type_is_flagged_once()
    {
        const string source = """
            using System.Net.Http;

            public static class ClientFactory
            {
                public static HttpClient Create() => null!;
            }

            public class Catalog
            {
                public void Fetch()
                {
                    HttpClient client = ClientFactory.Create();
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0001");
    }

    [Fact]
    public async Task Listening_on_a_port_is_flagged_with_NMP0003()
    {
        const string source = """
            using System.Net;
            using System.Net.Sockets;

            public class Seeder
            {
                public void Listen()
                {
                    TcpListener listener = new(IPAddress.Any, 6881);
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("NMP0003");
    }

    [Fact]
    public async Task A_type_that_is_not_banned_is_left_alone()
    {
        const string source = """
            using System.Text;

            public class Formatter
            {
                public void Build()
                {
                    StringBuilder builder = new();
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics
            .Should()
            .BeEmpty("a rule that fires on everything is a rule an author turns off");
    }

    [Fact]
    public async Task A_type_that_only_shares_a_name_is_left_alone()
    {
        // Matching on the short name would flag this, and a plugin with its own
        // HttpClient type would be unable to build with the analyzer on.
        const string source = """
            public class HttpClient
            {
            }

            public class Catalog
            {
                public void Fetch()
                {
                    HttpClient client = new();
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics = await AnalyzerHarness.RunAsync<BannedApiAnalyzer>(
            source
        );

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Every_rule_the_contract_declares_has_a_descriptor()
    {
        PluginAnalyzerDescriptors.All.Should().HaveCount(16);
        PluginAnalyzerDescriptors
            .All.Select(descriptor => descriptor.Id)
            .Should()
            .OnlyHaveUniqueItems();
        PluginDiagnostics.For("NMP0016").Title.ToString().Should().Contain("context.Scheduler");
    }

    [Fact]
    public void Every_diagnostic_the_analyzer_supports_is_one_the_contract_declares()
    {
        IEnumerable<string> declared = PluginAnalyzerDescriptors.All.Select(descriptor =>
            descriptor.Id
        );

        new BannedApiAnalyzer()
            .SupportedDiagnostics.Select(descriptor => descriptor.Id)
            .Should()
            .BeSubsetOf(declared);
    }
}
