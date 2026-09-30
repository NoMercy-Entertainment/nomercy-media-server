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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The files on disk are what load, so the install records a SHA-256 per file
/// and every load checks them: a changed, added or removed file is a refusal,
/// and so is a folder that was never installed.
/// </summary>
public class FileManifestVerificationStageTests : IDisposable
{
    private const string Echo = "NoMercy.Plugin.Samples.Echo";

    private readonly string _tempDir;

    public FileManifestVerificationStageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nomercy-file-manifest-" + Ulid.NewUlid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The Echo DLL beside a minimal manifest, the way an install leaves it.</summary>
    private string StageEchoPlugin()
    {
        string dll = CodeScanVerificationStageTests.StageAlone(_tempDir, Echo);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(dll)!, "plugin.json"),
            $$"""
            {
              "id": "{{Ulid.NewUlid()}}",
              "name": "Echo",
              "version": "1.2.0",
              "description": "echoes",
              "assembly": "{{Echo}}.dll",
              "autoEnabled": true
            }
            """
        );

        return dll;
    }

    private static PluginManifest Manifest() =>
        new()
        {
            Id = new PluginId(Ulid.NewUlid()),
            Name = "Echo",
            Description = "echoes",
            Version = "1.2.0",
            Assembly = $"{Echo}.dll",
        };

    private static PluginVerificationContext Context(string dll) =>
        new()
        {
            Manifest = Manifest(),
            AssemblyPath = dll,
            PackagePath = null,
        };

    private static async Task<string> StageAndRecordAsync(FileManifestVerificationStageTests tests)
    {
        string dll = tests.StageEchoPlugin();
        await PluginFileManifest.WriteAsync(Path.GetDirectoryName(dll)!, CancellationToken.None);

        return dll;
    }

    [Fact]
    public async Task AnInstalledFolderPasses()
    {
        string dll = await StageAndRecordAsync(this);

        (PluginStageOutcome outcome, string? message) =
            new FileManifestVerificationStage().Evaluate(Context(dll));

        outcome.Should().Be(PluginStageOutcome.Pass, message);
    }

    [Fact]
    public async Task AChangedDllFails()
    {
        string dll = await StageAndRecordAsync(this);
        await File.AppendAllTextAsync(dll, "x");

        (PluginStageOutcome outcome, string? message) =
            new FileManifestVerificationStage().Evaluate(Context(dll));

        outcome.Should().Be(PluginStageOutcome.Fail);
        message
            .Should()
            .Contain($"changed: {Echo}.dll")
            .And.Contain(PluginRefusalCode.FilesChanged);
    }

    [Fact]
    public async Task AnAddedDllFails()
    {
        string dll = await StageAndRecordAsync(this);
        File.Copy(dll, Path.Combine(Path.GetDirectoryName(dll)!, "Extra.dll"));

        (PluginStageOutcome outcome, string? message) =
            new FileManifestVerificationStage().Evaluate(Context(dll));

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("added: Extra.dll");
    }

    [Fact]
    public void AFolderWithNoManifestFails()
    {
        // A folder that was never installed through the server: copied by
        // hand, with no record beside it.
        string dll = StageEchoPlugin();
        File.Delete(Path.Combine(Path.GetDirectoryName(dll)!, PluginFileManifest.FileName));

        (PluginStageOutcome outcome, string? message) =
            new FileManifestVerificationStage().Evaluate(Context(dll));

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("no file manifest");
    }

    [Fact]
    public async Task TheDefaultVerifierChecksFiles()
    {
        string dll = await StageAndRecordAsync(this);
        await File.AppendAllTextAsync(dll, "x");

        PluginVerificationResult result = new PluginVerifier().Verify(Manifest(), dll, null);

        result.Verified.Should().BeFalse();
        result.Failures.Should().ContainMatch($"*changed: {Echo}.dll*");
    }
}
