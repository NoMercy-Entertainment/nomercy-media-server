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

using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugin.Cli;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins.Cli;

/// <summary>
/// The marketplace runs `nomercy-plugin verify --json &lt;zip&gt;` and reads the
/// object it prints. Each of these is a way that read could silently succeed
/// on a plugin that should have been refused.
/// </summary>
public class VerifyContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-verify-contract-" + Ulid.NewUlid()
    );

    public VerifyContractTests()
    {
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

    private static string Manifest(string targetAbi = "12.0", string label = "radio.nav.title") =>
        $$"""
            {
              "id": "5KTKRT4Z2Y9P59Y40W5CX4TQKF",
              "name": "Made Up",
              "description": "A made-up plugin.",
              "version": "1.0.0",
              "targetAbi": "{{targetAbi}}",
              "assembly": "Made.Up.dll",
              "capabilities": {
                "rest": true,
                "ui": { "mounts": [ { "section": "music", "label": "{{label}}", "route": "/" } ] }
              }
            }
            """;

    private string Zip(string? manifest, string inner = "Made.Up")
    {
        string source = Path.Combine(_root, "zip-src-" + Ulid.NewUlid());
        Directory.CreateDirectory(Path.Combine(source, inner));
        File.WriteAllText(Path.Combine(source, inner, "Made.Up.dll"), "x");

        if (manifest is not null)
            File.WriteAllText(Path.Combine(source, inner, "plugin.json"), manifest);

        string zip = Path.Combine(_root, "plugin-" + Ulid.NewUlid() + ".zip");
        ZipFile.CreateFromDirectory(source, zip);
        return zip;
    }

    private string Folder(string manifest)
    {
        string folder = Path.Combine(_root, "folder-" + Ulid.NewUlid());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest);
        return folder;
    }

    private static JsonElement Printed(ScanReport report)
    {
        StringWriter writer = new();
        VerifyCommand.Print(report, asJson: true, writer);
        return JsonDocument.Parse(writer.ToString()).RootElement;
    }

    [Fact]
    public async Task A_clean_zip_passes()
    {
        ScanReport report = await VerifyCommand.RunAsync(Zip(Manifest()));

        report.Refusals.Should().BeEmpty();
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task A_zip_with_a_blocked_manifest_fails_the_run()
    {
        ScanReport report = await VerifyCommand.RunAsync(Zip(Manifest(targetAbi: "9.0")));

        report.ExitCode.Should().Be(1);
        report
            .Refusals.Should()
            .Contain(refusal => refusal.Code == PluginRefusalCodes.AbiUnsupported);
    }

    [Fact]
    public async Task A_zip_with_the_manifest_at_its_root_is_read_too()
    {
        ScanReport report = await VerifyCommand.RunAsync(Zip(Manifest(), inner: "."));

        report.Refusals.Should().BeEmpty();
    }

    [Fact]
    public async Task A_zip_with_no_manifest_is_blocked_and_names_the_zip_not_a_temp_folder()
    {
        string zip = Zip(manifest: null);

        ScanReport report = await VerifyCommand.RunAsync(zip);

        report.ExitCode.Should().Be(1);
        report.Refusals.Should().ContainSingle().Which.Plugin.Should().Be(Path.GetFileName(zip));
    }

    [Fact]
    public async Task The_folder_a_zip_was_unpacked_into_is_gone_afterwards()
    {
        string pattern = "nomercy-verify-*";
        int before = Directory.GetDirectories(Path.GetTempPath(), pattern).Length;

        await VerifyCommand.RunAsync(Zip(Manifest()));

        Directory.GetDirectories(Path.GetTempPath(), pattern).Length.Should().Be(before);
    }

    [Fact]
    public async Task A_folder_is_still_scanned_as_before()
    {
        ScanReport report = await VerifyCommand.RunAsync(Folder(Manifest(targetAbi: "9.0")));

        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task The_json_is_an_object_with_passed_and_findings()
    {
        JsonElement printed = Printed(await VerifyCommand.RunAsync(Zip(Manifest())));

        printed.ValueKind.Should().Be(JsonValueKind.Object);
        printed.GetProperty("passed").GetBoolean().Should().BeTrue();
        printed.GetProperty("findings").GetArrayLength().Should().Be(0);
        printed.GetProperty("capabilitiesUsed").ValueKind.Should().Be(JsonValueKind.Array);
        printed.GetProperty("dependencies").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task A_blocked_finding_prints_passed_false_with_every_field()
    {
        JsonElement printed = Printed(
            await VerifyCommand.RunAsync(Zip(Manifest(targetAbi: "9.0")))
        );

        printed.GetProperty("passed").GetBoolean().Should().BeFalse();
        JsonElement finding = printed.GetProperty("findings")[0];
        finding.GetProperty("code").GetString().Should().Be(PluginRefusalCodes.AbiUnsupported);
        finding.GetProperty("severity").GetString().Should().Be("blocked");
        foreach (string field in new[] { "plugin", "what", "why", "fix", "marketplaceCode" })
            finding.TryGetProperty(field, out _).Should().BeTrue(field);
    }

    [Fact]
    public async Task A_warning_alone_still_passes_and_is_still_listed()
    {
        JsonElement printed = Printed(
            await VerifyCommand.RunAsync(Zip(Manifest(label: "Made Up Plugin")))
        );

        printed.GetProperty("passed").GetBoolean().Should().BeTrue();
        printed
            .GetProperty("findings")[0]
            .GetProperty("severity")
            .GetString()
            .Should()
            .Be("degraded");
    }

    [Theory]
    [InlineData(PluginRefusalCodes.CapabilityNotDeclared, "MARKETPLACE_SCAN_UNDECLARED_CAPABILITY")]
    [InlineData(PluginRefusalCodes.CodeScan, "MARKETPLACE_SCAN_BANNED_API")]
    [InlineData(PluginRefusalCodes.ManifestInvalid, null)]
    public void A_cli_code_maps_to_the_marketplace_code_the_scanner_reads(
        string pluginCode,
        string? expected
    )
    {
        MarketplaceCodeMap.For(pluginCode).Should().Be(expected);
    }

    [Theory]
    [InlineData(new[] { "verify", "--json", "/artifact/x.zip" }, "/artifact/x.zip")]
    [InlineData(new[] { "verify", "/pkg", "--json" }, "/pkg")]
    [InlineData(new[] { "verify", "." }, ".")]
    [InlineData(new[] { "pack", "--output", "/out", "/src" }, "/src")]
    [InlineData(new[] { "pack", "/src", "--feed", "/feed", "--output", "/out" }, "/src")]
    public void The_path_is_found_wherever_the_flags_are(string[] args, string expected)
    {
        CliArguments.Target(args).Should().Be(expected);
    }

    public static TheoryData<string[]> NoPath =>
        new()
        {
            new[] { "verify" },
            new[] { "verify", "--json" },
            new[] { "pack", "--output", "/out" },
        };

    [Theory]
    [MemberData(nameof(NoPath))]
    public void With_no_path_the_current_folder_is_used(string[] args)
    {
        CliArguments.Target(args).Should().Be(Directory.GetCurrentDirectory());
    }
}
