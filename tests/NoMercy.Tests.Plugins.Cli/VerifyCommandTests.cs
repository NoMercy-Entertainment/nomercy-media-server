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
using NoMercy.Plugin.Cli;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins.Cli;

/// <summary>
/// The local scan has to agree with the marketplace's. One that is kinder
/// passes a plugin that is then rejected on upload, and the author has no way
/// to tell which of the two was right.
/// </summary>
public class VerifyCommandTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception) { }
    }

    private string Write(string manifest)
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-cli-" + Ulid.NewUlid());
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest);
        return folder;
    }

    private const string Clean = """
        {
          "id": "5KTKRT4Z2Y9P59Y40W5CX4TQKF",
          "name": "Internet Radio",
          "description": "Radio.",
          "version": "2.0.0",
          "targetAbi": "12.0",
          "assembly": "Radio.dll",
          "capabilities": {
            "rest": true,
            "ui": {
              "mounts": [
                { "section": "music", "label": "radio.nav.title", "route": "/" }
              ]
            }
          }
        }
        """;

    [Fact]
    public async Task A_clean_manifest_verifies()
    {
        ScanReport report = await VerifyCommand.RunAsync(Write(Clean));

        report.Refusals.Should().BeEmpty();
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task A_folder_with_no_manifest_is_blocked()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-cli-" + Ulid.NewUlid());
        Directory.CreateDirectory(folder);
        _folders.Add(folder);

        ScanReport report = await VerifyCommand.RunAsync(folder);

        report.ExitCode.Should().Be(1);
        report.Refusals.Should().ContainSingle().Which.Code.Should().Be("PLUGIN_MANIFEST_INVALID");
    }

    [Fact]
    public async Task A_manifest_that_does_not_parse_is_blocked_and_says_so_once()
    {
        ScanReport report = await VerifyCommand.RunAsync(Write("{ not json"));

        report.ExitCode.Should().Be(1);
        report
            .Refusals.Should()
            .ContainSingle("every other finding below a broken manifest would be a guess");
    }

    [Fact]
    public async Task An_abi_two_majors_behind_is_blocked()
    {
        ScanReport report = await VerifyCommand.RunAsync(
            Write(Clean.Replace("\"targetAbi\": \"12.0\"", "\"targetAbi\": \"9.0\""))
        );

        report.ExitCode.Should().Be(1);
        report
            .Refusals.Should()
            .Contain(refusal => refusal.Code == PluginRefusalCodes.AbiUnsupported);
    }

    /// <summary>
    /// A major usually only removes members, so the one behind it still loads
    /// and warning is the honest answer. Twelve renamed the SDK assembly and
    /// every namespace in it, so an eleven plugin resolves no type at all:
    /// passing the build here would hand the author a green run and a server
    /// that refuses to load what it produced.
    /// </summary>
    [Fact]
    public async Task An_abi_the_server_will_not_load_fails_rather_than_warns()
    {
        ScanReport report = await VerifyCommand.RunAsync(
            Write(
                Clean.Replace(
                    "\"targetAbi\": \"12.0\"",
                    $"\"targetAbi\": \"{PluginAbi.Oldest.Major - 1}.0\""
                )
            )
        );

        PluginAbi
            .IsCompatible($"{PluginAbi.Oldest.Major - 1}.0")
            .Should()
            .BeFalse("this test says nothing if the server would load it after all");
        report.ExitCode.Should().Be(1);
        report
            .Refusals.Should()
            .Contain(refusal => refusal.Code == PluginRefusalCodes.AbiUnsupported);
    }

    [Fact]
    public async Task A_capability_the_vocabulary_does_not_know_is_blocked()
    {
        ScanReport report = await VerifyCommand.RunAsync(
            Write(Clean.Replace("\"rest\": true", "\"rest\": true, \"hooks\": [\"not.a.thing\"]"))
        );

        report.ExitCode.Should().Be(1);
        report.Refusals.Should().Contain(refusal => refusal.What.Contains("not.a.thing"));
    }

    [Fact]
    public async Task A_capability_the_vocabulary_does_know_is_left_alone()
    {
        ScanReport report = await VerifyCommand.RunAsync(
            Write(Clean.Replace("\"rest\": true", "\"rest\": true, \"hooks\": [\"library.read\"]"))
        );

        report.Refusals.Should().BeEmpty("the check must catch the unknown name, not every name");
    }

    [Fact]
    public async Task A_mount_naming_a_kind_that_does_not_exist_is_blocked()
    {
        ScanReport report = await VerifyCommand.RunAsync(
            Write(Clean.Replace("\"section\": \"music\"", "\"section\": \"backend\""))
        );

        report.ExitCode.Should().Be(1);
        report.Refusals.Should().Contain(refusal => refusal.Fix.Contains("music"));
    }

    [Fact]
    public async Task A_label_written_as_a_sentence_warns()
    {
        ScanReport report = await VerifyCommand.RunAsync(
            Write(Clean.Replace("\"label\": \"radio.nav.title\"", "\"label\": \"Internet Radio\""))
        );

        report.ExitCode.Should().Be(0);
        report
            .Refusals.Should()
            .Contain(refusal => refusal.Severity == PluginRefusalSeverity.Degraded);
    }

    [Fact]
    public async Task The_json_output_is_what_a_build_step_reads()
    {
        ScanReport report = await VerifyCommand.RunAsync(
            Write(Clean.Replace("\"targetAbi\": \"12.0\"", "\"targetAbi\": \"9.0\""))
        );
        StringWriter writer = new();

        VerifyCommand.Print(report, asJson: true, writer);

        writer
            .ToString()
            .Should()
            .Contain(
                PluginRefusalCodes.AbiUnsupported,
                "a CI step that has to parse a paragraph breaks on a reword"
            );
    }

    [Fact]
    public async Task A_clean_run_says_so_rather_than_printing_nothing()
    {
        ScanReport report = await VerifyCommand.RunAsync(Write(Clean));
        StringWriter writer = new();

        VerifyCommand.Print(report, asJson: false, writer);

        writer.ToString().Should().Contain("Nothing to report");
    }
}
