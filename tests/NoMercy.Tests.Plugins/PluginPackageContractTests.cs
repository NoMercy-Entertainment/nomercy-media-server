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

using System.Xml.Linq;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What a plugin author downloads has to match what the server runs.
/// <para>
/// Both of the plugins on the server today reference the abstractions at
/// <c>*</c>, which is what an author does when versions are unpredictable.
/// These pin the other side of that: the packages version with the contract,
/// so <c>11.0.*</c> in a plugin resolves to something its manifest actually
/// claims compatibility with.
/// </para>
/// </summary>
public class PluginPackageContractTests
{
    private static readonly string[] PluginPackages =
    [
        "src/NoMercy.Plugins.Abstractions/NoMercy.Plugins.Abstractions.csproj",
        "src/NoMercy.Plugins.Mvc/NoMercy.Plugins.Mvc.csproj",
        "src/NoMercy.Plugins.Testing/NoMercy.Plugins.Testing.csproj",
        "src/NoMercy.Plugins.Analyzers/NoMercy.Plugins.Analyzers.csproj",
        "src/NoMercy.Design/NoMercy.Design.csproj",
        "src/NoMercy.Events/NoMercy.Events.csproj",
    ];

    private static XDocument Project(string relativePath) =>
        XDocument.Parse(
            File.ReadAllText(RepoPaths.At(relativePath.Replace('/', Path.DirectorySeparatorChar)))
        );

    private static string? Value(XDocument project, string element) =>
        project.Descendants(element).FirstOrDefault()?.Value;

    [Fact]
    public void Every_plugin_package_is_packable()
    {
        foreach (string path in PluginPackages)
            Value(Project(path), "IsPackable")
                .Should()
                .Be("true", $"{path} is one an author outside this repository has to download");
    }

    [Fact]
    public void Every_plugin_package_versions_with_the_contract_not_the_server()
    {
        foreach (string path in PluginPackages)
            Value(Project(path), "Version")
                .Should()
                .Be(
                    "$(PluginPackageVersion)",
                    $"{path} pinned to the server's version would resolve to something no manifest claims"
                );
    }

    [Fact]
    public void The_contract_version_property_matches_the_abi_the_code_reports()
    {
        string? declared = Value(Project("Directory.Build.props"), "PluginPackageVersion");

        declared.Should().NotBeNull();
        Version
            .Parse(declared!)
            .Should()
            .Match<Version>(
                version =>
                    version.Major == PluginAbi.Current.Major
                    && version.Minor == PluginAbi.Current.Minor,
                "a package that does not carry the contract's own version is one the template cannot ask for"
            );
    }

    [Fact]
    public void The_plugin_packages_share_one_prefix_so_they_are_found_together()
    {
        IEnumerable<string> ids = PluginPackages.Select(path => Value(Project(path), "PackageId")!);

        ids.Should()
            .OnlyContain(
                id => id.StartsWith("NoMercy.Plugins.", StringComparison.Ordinal),
                "an author searching for the SDK finds one prefix rather than four unrelated names"
            );
    }

    [Fact]
    public void Every_plugin_package_id_is_unique()
    {
        PluginPackages
            .Select(path => Value(Project(path), "PackageId"))
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_template_asks_for_the_contract_major_rather_than_anything()
    {
        string csproj = File.ReadAllText(
            RepoPaths.At(
                Path.Combine(
                    "templates",
                    "NoMercy.Plugin.Template",
                    "NoMercy.Plugin.Template.csproj"
                )
            )
        );

        csproj
            .Should()
            .Contain(
                $"Version=\"{PluginAbi.Current.Major}.*\"",
                "the major is the compatibility promise; pinning the minor would make every additive change re-pin every plugin"
            );
    }
}
