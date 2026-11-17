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
using NoMercy.Design;
using NoMercy.PluginSdk.Analyzers;
using Xunit;

namespace NoMercy.Tests.Plugins.Analyzers;

public class ShelfPropsIdAnalyzerTests
{
    /// <summary>
    /// A stand-in for the SDK's node, under its real full name. The rule matches
    /// on that name, so this is what a plugin's compilation looks like to it.
    /// </summary>
    private const string Sdk = """
        using System.Collections.Generic;

        namespace NoMercy.PluginSdk.Abstractions
        {
            public class PluginComponent
            {
                public string Id { get; init; } = "";
                public string Component { get; init; } = "";
                public Dictionary<string, object?> Props { get; init; } = new();
                public List<PluginComponent> Items { get; init; } = new();
                public object? Design { get; init; }
            }
        }

        namespace Sample
        {
            public record NMGridProps
            {
                public string? Id { get; init; }
                public string? Title { get; init; }
            }
        }

        namespace Sample
        {
            public static class Names
            {
                public const string Grid = "NMGrid";
            }
        }
        """;

    [Theory]
    [InlineData("NMGrid")]
    [InlineData("NMMusicCard")]
    [InlineData("NMCarousel")]
    [InlineData("NMLolomo")]
    public async Task A_shelf_whose_props_have_no_id_is_flagged(string component)
    {
        IReadOnlyList<Diagnostic> diagnostics = await RunAsync(
            $$"""
            PluginComponent shelf = new()
            {
                Id = "all",
                Component = "{{component}}",
                Props = new() { ["title"] = "All" },
            };
            """
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0017");
        diagnostics[0].GetMessage().Should().Contain("PluginShelves");
    }

    [Fact]
    public async Task A_shelf_with_no_props_at_all_is_flagged()
    {
        IReadOnlyList<Diagnostic> diagnostics = await RunAsync(
            """PluginComponent shelf = new() { Id = "all", Component = "NMGrid" };"""
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0017");
    }

    [Fact]
    public async Task A_component_name_from_a_constant_is_read()
    {
        IReadOnlyList<Diagnostic> diagnostics = await RunAsync(
            """
            PluginComponent shelf = new PluginComponent
            {
                Id = "all",
                Component = Sample.Names.Grid,
                Props = new Dictionary<string, object?> { { "title", "All" } },
            };
            """
        );

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("NMP0017");
    }

    [Theory]
    [InlineData("""new() { ["id"] = "all" }""")]
    [InlineData("""new() { ["title"] = "All", ["id"] = "all" }""")]
    [InlineData("""new Dictionary<string, object?> { { "id", "all" } }""")]
    // Props the rule cannot see into are never guessed at.
    [InlineData("Built()")]
    [InlineData("new(Built())")]
    [InlineData("""new() { [Key()] = "all" }""")]
    public async Task A_shelf_that_has_or_may_have_its_id_is_left_alone(string props)
    {
        IReadOnlyList<Diagnostic> diagnostics = await RunAsync(
            $$"""
            PluginComponent shelf = new()
            {
                Id = "all",
                Component = "NMGrid",
                Props = {{props}},
            };
            """
        );

        diagnostics.Should().BeEmpty();
    }

    [Theory]
    // Design is merged into the props under its own JSON names, which the rule
    // cannot see, so neither of these can be proven to miss the id.
    [InlineData("""Design = new NMGridProps { Id = "all" },""")]
    [InlineData("""Design = new NMGridProps { Title = "All" },""")]
    [InlineData(
        """Design = new NMGridProps { Title = "All" }, Props = new() { ["title"] = "All" },"""
    )]
    public async Task A_shelf_with_a_design_record_is_left_alone(string design)
    {
        IReadOnlyList<Diagnostic> diagnostics = await RunAsync(
            $$"""
            PluginComponent shelf = new()
            {
                Id = "all",
                Component = "NMGrid",
                {{design}}
            };
            """
        );

        diagnostics.Should().BeEmpty();
    }

    [Theory]
    // The design system's card reads no id from its props.
    [InlineData("NMCard")]
    [InlineData("NMText")]
    public async Task Another_component_without_an_id_is_left_alone(string component)
    {
        IReadOnlyList<Diagnostic> diagnostics = await RunAsync(
            $$"""
            PluginComponent node = new() { Id = "x", Component = "{{component}}" };
            """
        );

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_type_of_the_same_short_name_elsewhere_is_left_alone()
    {
        const string source = """
            namespace Mine
            {
                public class PluginComponent
                {
                    public string Component { get; init; } = "";
                }

                public class Catalog
                {
                    public object Build() => new PluginComponent { Component = "NMGrid" };
                }
            }
            """;

        IReadOnlyList<Diagnostic> diagnostics =
            await AnalyzerHarness.RunAsync<ShelfPropsIdAnalyzer>(source);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void The_rule_names_the_components_the_sdk_shelves_are_built_from()
    {
        ShelfPropsIdAnalyzer
            .Components.Should()
            .BeEquivalentTo([
                NmAppComponents.Grid,
                NmAppComponents.MusicCard,
                NmAppComponents.Carousel,
                NmAppComponents.Lolomo,
            ]);
    }

    [Fact]
    public void Every_diagnostic_the_rule_supports_is_one_the_contract_declares()
    {
        new ShelfPropsIdAnalyzer()
            .SupportedDiagnostics.Select(descriptor => descriptor.Id)
            .Should()
            .BeSubsetOf(PluginAnalyzerDescriptors.All.Select(descriptor => descriptor.Id));
    }

    private static Task<IReadOnlyList<Diagnostic>> RunAsync(string statement) =>
        AnalyzerHarness.RunAsync<ShelfPropsIdAnalyzer>(
            $$"""
            {{Sdk}}

            namespace Sample
            {
                using System.Collections.Generic;
                using NoMercy.PluginSdk.Abstractions;

                public class View
                {
                    private static Dictionary<string, object?> Built() => new() { ["id"] = "all" };

                    private static string Key() => "id";

                    public void Build()
                    {
                        {{statement}}
                    }
                }
            }
            """
        );
}
