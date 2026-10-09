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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The server never runs the IL code scan when it loads or installs a plugin.
/// The owner chose the plugin (marketplace or a zip they picked) and the
/// plugin runs in process, so a DLL that references a type the scan bans
/// (System.Diagnostics.Process, P/Invoke, reflection) still loads. The scan
/// verdict belongs to the marketplace pipeline and to the plugin author's own
/// check; <see cref="CodeScanVerificationStage" /> stays for those callers.
/// </summary>
public class PluginLoadWithoutCodeScanTests : IDisposable
{
    private const string Escapes = "NoMercy.Plugin.Samples.Escapes";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "nomercy-load-no-scan-" + Ulid.NewUlid()
    );

    public PluginLoadWithoutCodeScanTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task BareAssemblyLoad_OfAPluginThatReferencesBannedTypes_IsNotRefusedByACodeScan()
    {
        string dll = CodeScanVerificationStageTests.StageAlone(_tempDir, Escapes);
        InMemoryEventBus eventBus = new();
        PluginRegistry registry = new();
        List<PluginErrorOccurredEvent> errors = [];
        eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );
        PluginLoader loader = new(
            eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir),
            registry,
            new PluginVerifier([]),
            new PluginConsentService(new InMemoryConsentStore()),
            TestPluginPlatform.ContextFactory(eventBus, TestStorageHelper.CreateStorage(_tempDir))
        );

        await loader.LoadPluginAssemblyAsync(dll);

        errors
            .Select(e => e.ErrorMessage)
            .Should()
            .NotContain(
                m => m.Contains(PluginRefusalCode.CodeScan),
                "the server does not scan plugin code at load"
            );
    }

    [Fact]
    public void DefaultVerifier_DoesNotFailAPluginThatReferencesBannedTypes_OnTheScan()
    {
        string dll = CodeScanVerificationStageTests.StageAlone(_tempDir, Escapes);
        PluginManifest manifest = new()
        {
            Id = new PluginId(Ulid.NewUlid()),
            Name = Escapes,
            Description = "d",
            Version = "1.0.0",
            Assembly = $"{Escapes}.dll",
            TargetAbi = PluginAbi.Current.ToString(),
        };

        PluginVerificationResult result = new PluginVerifier().Verify(manifest, dll, null);

        result.Failures.Should().NotContain(f => f.Contains(PluginRefusalCode.CodeScan));
    }

    [Fact]
    public async Task ArchiveInstall_OfAPluginThatReferencesBannedTypes_IsNotRefusedByACodeScan()
    {
        string pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string zip = Path.Combine(_tempDir, "escapes.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (StreamWriter writer = new(archive.CreateEntry($"{Escapes}/plugin.json").Open()))
                writer.Write(
                    $$"""
                    {
                      "id": "5KTKRT4Z2Y9P59Y40W5CX4TQKF",
                      "name": "Escapes",
                      "description": "References banned types.",
                      "version": "1.0.0",
                      "targetAbi": "12.0",
                      "author": "NoMercy Community",
                      "assembly": "{{Escapes}}.dll"
                    }
                    """
                );
            archive.CreateEntryFromFile(
                CodeScanVerificationStageTests.SampleDllPath(Escapes),
                $"{Escapes}/{Escapes}.dll"
            );
        }

        using PluginManager manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            pluginsDir,
            TestStorageHelper.CreateStorage(pluginsDir),
            TestStorageHelper.CreateBackend()
        );

        Exception? thrown = null;
        try
        {
            await manager.InstallPluginArchiveAsync(zip, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        (thrown?.Message ?? string.Empty)
            .Should()
            .NotContain(PluginRefusalCode.CodeScan, "the server does not scan plugin code");
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
