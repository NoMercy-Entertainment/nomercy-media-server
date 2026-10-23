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

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugin.Cli;
using Xunit;

namespace NoMercy.Tests.Plugins.Cli;

/// <summary>
/// The marketplace signs whatever pack produces, so each of these is a way a
/// build could hand it something it should not: a guess at which project is the
/// plugin, a package fetched from somewhere nobody agreed to, or a zip that
/// carries the host's own assemblies.
/// </summary>
public class PackCommandTests : IClassFixture<PackFeedFixture>, IDisposable
{
    private readonly PackFeedFixture _fixture;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-pack-" + Ulid.NewUlid()
    );

    public PackCommandTests(PackFeedFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { }
    }

    private static string Manifest(string targetAbi = "12.0") =>
        $$"""
            {
              "id": "5KTKRT4Z2Y9P59Y40W5CX4TQKF",
              "name": "Made Up",
              "description": "A made-up plugin.",
              "version": "1.0.0",
              "targetAbi": "{{targetAbi}}",
              "assembly": "Made.Up.dll",
              "capabilities": { "rest": true }
            }
            """;

    private static string Project(string packageReferences) =>
        $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Made.Up</AssemblyName>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
              <ItemGroup>
                {packageReferences}
              </ItemGroup>
              <ItemGroup>
                <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """;

    private const string SdkReference =
        """<PackageReference Include="NoMercy.PluginSdk.Abstractions" Version="12.6.0" />""";

    private string WritePlugin(
        string? manifest,
        string packageReferences = SdkReference,
        string folder = "src/Made.Up"
    )
    {
        string source = Path.Combine(_root, "source");
        string project = Path.Combine(source, folder);
        Directory.CreateDirectory(project);

        File.WriteAllText(Path.Combine(project, "Made.Up.csproj"), Project(packageReferences));
        File.WriteAllText(
            Path.Combine(project, "Plugin.cs"),
            "namespace MadeUp; public sealed class Plugin { public string Id => NoMercy_PluginSdk_Abstractions.Marker.Name; }"
        );

        if (manifest is not null)
            File.WriteAllText(Path.Combine(project, "plugin.json"), manifest);

        return source;
    }

    private PackOptions Options(string source) =>
        new(source, Path.Combine(_root, "out"), _fixture.Feed, _fixture.Packages);

    [Fact]
    public async Task A_source_with_no_manifest_is_refused()
    {
        string source = WritePlugin(manifest: null);

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should().ThrowAsync<PackException>().WithMessage("*plugin.json*");
    }

    [Fact]
    public async Task A_source_with_two_manifests_is_refused_rather_than_guessed()
    {
        string source = WritePlugin(Manifest());
        string other = Path.Combine(source, "src", "Other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "plugin.json"), Manifest());
        File.WriteAllText(Path.Combine(other, "Other.csproj"), Project(SdkReference));

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should().ThrowAsync<PackException>().WithMessage("*more than one*");
    }

    [Fact]
    public async Task A_manifest_with_no_project_beside_it_is_refused()
    {
        string source = WritePlugin(Manifest());
        File.Delete(Path.Combine(source, "src", "Made.Up", "Made.Up.csproj"));

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should().ThrowAsync<PackException>().WithMessage("*csproj*");
    }

    [Fact]
    public async Task A_manifest_with_two_projects_beside_it_is_refused()
    {
        string source = WritePlugin(Manifest());
        File.WriteAllText(
            Path.Combine(source, "src", "Made.Up", "Second.csproj"),
            Project(SdkReference)
        );

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should().ThrowAsync<PackException>().WithMessage("*more than one*");
    }

    [Fact]
    public async Task A_blocked_manifest_is_refused_with_the_scan_finding()
    {
        string source = WritePlugin(Manifest(targetAbi: "not-a-version"));

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should().ThrowAsync<PackException>().WithMessage("*targetAbi*");
    }

    [Fact]
    public async Task A_missing_offline_feed_is_refused_before_anything_is_restored()
    {
        string source = WritePlugin(Manifest());

        Func<Task> act = () =>
            PackCommand.RunAsync(new(source, Path.Combine(_root, "out"), FeedFolder: null));

        await act.Should().ThrowAsync<PackException>().WithMessage("*offline*feed*");
    }

    [Fact]
    public async Task A_manifest_claiming_a_newer_abi_than_the_sdk_it_built_against_is_refused()
    {
        string source = WritePlugin(
            Manifest(targetAbi: "12.5"),
            """<PackageReference Include="NoMercy.PluginSdk.Abstractions" Version="12.2.0" />"""
        );

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should().ThrowAsync<PackException>().WithMessage("*12.5*12.2*");
    }

    [Fact]
    public async Task A_package_missing_from_the_offline_feed_fails_the_build_and_names_it()
    {
        string source = WritePlugin(
            Manifest(),
            SdkReference + """<PackageReference Include="Not.In.The.Feed" Version="1.0.0" />"""
        );

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should()
            .ThrowAsync<PackException>()
            .WithMessage("*offline feed*")
            .WithMessage("*Not.In.The.Feed*");
    }

    [Fact]
    public async Task A_nuget_config_inside_the_source_cannot_add_a_package_source()
    {
        string source = WritePlugin(
            Manifest(),
            SdkReference + """<PackageReference Include="Third.Party" Version="1.0.0" />"""
        );
        File.WriteAllText(
            Path.Combine(source, "nuget.config"),
            $"""
            <configuration>
              <packageSources>
                <add key="rogue" value="{_fixture.Rogue}" />
              </packageSources>
            </configuration>
            """
        );

        Func<Task> act = () => PackCommand.RunAsync(Options(source));

        await act.Should().ThrowAsync<PackException>().WithMessage("*Third.Party*");
    }

    [Fact]
    public async Task A_good_plugin_yields_exactly_one_zip_and_its_manifest()
    {
        string source = WritePlugin(Manifest());
        PackOptions options = Options(source);

        PackResult result = await PackCommand.RunAsync(options);

        Directory
            .GetFiles(options.OutputFolder)
            .Select(Path.GetFileName)
            .Should()
            .BeEquivalentTo("Made.Up-1.0.0.zip", "plugin.json");
        File.ReadAllText(result.ManifestPath).Should().Contain("\"targetAbi\": \"12.0\"");

        using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
        List<string> entries = zip.Entries.Select(entry => entry.FullName).ToList();
        entries.Should().Contain("Made.Up/Made.Up.dll");
        entries.Should().Contain("Made.Up/plugin.json");
        entries.Should().Contain("Made.Up/Made.Up.deps.json");
    }

    [Fact]
    public async Task The_zip_carries_no_assembly_the_host_already_owns()
    {
        string source = WritePlugin(Manifest());

        PackResult result = await PackCommand.RunAsync(Options(source));

        using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
        zip.Entries.Select(entry => entry.Name)
            .Should()
            .NotContain("NoMercy.PluginSdk.Abstractions.dll");
        zip.Entries.Select(entry => entry.Name).Should().NotContain(name => name.EndsWith(".pdb"));
    }

    [Fact]
    public async Task A_third_party_package_in_the_feed_ships_beside_the_plugin()
    {
        string source = WritePlugin(
            Manifest(),
            SdkReference + """<PackageReference Include="Third.Party" Version="1.0.0" />"""
        );
        File.AppendAllText(
            Path.Combine(source, "src", "Made.Up", "Plugin.cs"),
            "\npublic static class Uses { public static string Name => Third_Party.Marker.Name; }"
        );
        PackOptions options = new(
            source,
            Path.Combine(_root, "out"),
            _fixture.FeedWithThirdParty,
            _fixture.Packages
        );

        PackResult result = await PackCommand.RunAsync(options);

        using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
        zip.Entries.Select(entry => entry.Name).Should().Contain("Third.Party.dll");
    }

    [Fact]
    public async Task The_source_folder_is_left_exactly_as_it_was()
    {
        string source = WritePlugin(Manifest());
        List<string> before = Directory
            .GetFileSystemEntries(source, "*", SearchOption.AllDirectories)
            .Order()
            .ToList();

        await PackCommand.RunAsync(Options(source));

        Directory
            .GetFileSystemEntries(source, "*", SearchOption.AllDirectories)
            .Order()
            .Should()
            .Equal(before);
    }

    [Fact]
    public async Task An_output_folder_that_already_holds_a_zip_is_refused()
    {
        string source = WritePlugin(Manifest());
        PackOptions options = Options(source);
        Directory.CreateDirectory(options.OutputFolder);
        File.WriteAllText(Path.Combine(options.OutputFolder, "stale.zip"), "old");

        Func<Task> act = () => PackCommand.RunAsync(options);

        await act.Should().ThrowAsync<PackException>().WithMessage("*already*zip*");
    }
}

/// <summary>
/// Stand-ins for the contract and one third-party package, packed once into
/// local feeds. Real packages would put a network call into a unit test.
/// </summary>
public sealed class PackFeedFixture : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-pack-feed-" + Ulid.NewUlid()
    );

    public string Feed => Path.Combine(_root, "feed");
    public string FeedWithThirdParty => Path.Combine(_root, "feed-third");
    public string Rogue => Path.Combine(_root, "rogue");
    public string Packages => Path.Combine(_root, "packages");

    public PackFeedFixture()
    {
        Directory.CreateDirectory(Packages);

        PackStub(Feed, "NoMercy.PluginSdk.Abstractions", "12.6.0");
        PackStub(Feed, "NoMercy.PluginSdk.Abstractions", "12.2.0");
        PackStub(Rogue, "Third.Party", "1.0.0");

        Directory.CreateDirectory(FeedWithThirdParty);
        foreach (string file in Directory.GetFiles(Feed))
            File.Copy(file, Path.Combine(FeedWithThirdParty, Path.GetFileName(file)));
        foreach (string file in Directory.GetFiles(Rogue))
            File.Copy(file, Path.Combine(FeedWithThirdParty, Path.GetFileName(file)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { }
    }

    private void PackStub(string feed, string id, string version)
    {
        string project = Path.Combine(_root, "stub-" + id + "-" + version);
        Directory.CreateDirectory(project);
        string ns = id.Replace('.', '_');

        File.WriteAllText(
            Path.Combine(project, "Stub.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>{id}</PackageId>
                <AssemblyName>{id}</AssemblyName>
                <Version>{version}</Version>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
            </Project>
            """
        );
        File.WriteAllText(
            Path.Combine(project, "Marker.cs"),
            $"namespace {ns}; public static class Marker {{ public static string Name => \"{id}\"; }}"
        );

        ProcessStartInfo start = new("dotnet") { WorkingDirectory = project };
        foreach (string argument in new[] { "pack", "-c", "Release", "-o", feed })
            start.ArgumentList.Add(argument);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["NUGET_PACKAGES"] = Packages;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Packing {id} {version} failed:\n{output}");
    }
}
