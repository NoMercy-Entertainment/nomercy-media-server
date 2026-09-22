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
using NoMercy.PluginSdk.Abstractions;
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
        "src/NoMercy.PluginSdk.Abstractions/NoMercy.PluginSdk.Abstractions.csproj",
        "src/NoMercy.PluginSdk.Mvc/NoMercy.PluginSdk.Mvc.csproj",
        "src/NoMercy.PluginSdk.Testing/NoMercy.PluginSdk.Testing.csproj",
        "src/NoMercy.PluginSdk.Analyzers/NoMercy.PluginSdk.Analyzers.csproj",
    ];

    /// <summary>
    /// Built here, shipped inside the SDK package rather than beside it.
    /// <para>
    /// An author installs one thing, not three that have to be kept on the same
    /// version by hand, and nothing ever referenced these two alone.
    /// </para>
    /// </summary>
    private static readonly string[] CarriedInsideTheSdk =
    [
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

    /// <summary>
    /// The two that travel inside the SDK must not also be published on their
    /// own. A project that is both would put the same assembly in a consumer's
    /// output twice, from two packages that can drift to different versions.
    /// </summary>
    [Fact]
    public void What_ships_inside_the_sdk_is_not_published_beside_it()
    {
        foreach (string path in CarriedInsideTheSdk)
            Value(Project(path), "IsPackable")
                .Should()
                .Be("false", $"{path} ships inside the SDK package rather than as its own");
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
                id => id.StartsWith("NoMercy.PluginSdk.", StringComparison.Ordinal),
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
