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

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginManagerVerificationTests : IDisposable
{
    private readonly string _tempPluginsDir;
    private readonly PluginManager _manager;

    public PluginManagerVerificationTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-plugin-verify-tests-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);

        _manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend(),
            new PluginVerifier()
        );
    }

    public void Dispose()
    {
        _manager.Dispose();

        try
        {
            if (Directory.Exists(_tempPluginsDir))
            {
                Directory.Delete(_tempPluginsDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }
    }

    private string WriteSourceDll(byte[] bytes)
    {
        string path = Path.Combine(_tempPluginsDir, $"source-{Ulid.NewUlid():N}.dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string InstalledPathFor(string sourceDll)
    {
        string pluginName = Path.GetFileNameWithoutExtension(sourceDll);
        return Path.Combine(_tempPluginsDir, pluginName, Path.GetFileName(sourceDll));
    }

    [Fact]
    public async Task InstallPluginAsync_ChecksumMismatch_ThrowsAndDoesNotCopy()
    {
        string sourceDll = WriteSourceDll([9, 9, 9]);

        Func<Task> act = () => _manager.InstallPluginAsync(sourceDll, expectedChecksum: "deadbeef");

        await act.Should().ThrowAsync<PluginVerificationException>();
        File.Exists(InstalledPathFor(sourceDll)).Should().BeFalse();
    }

    /// <summary>
    /// A checksum describes the plugin's .zip package, so hashing a bare
    /// assembly is a published checksum nobody can reproduce. It is refused
    /// with the .zip named, rather than quietly accepted on this one route and
    /// rejected on the archive route.
    /// </summary>
    [Fact]
    public async Task InstallPluginAsync_ChecksumOverABareAssembly_IsRefusedAndDoesNotCopy()
    {
        byte[] bytes = [1, 2, 3, 4, 5];
        string sourceDll = WriteSourceDll(bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Func<Task> act = () => _manager.InstallPluginAsync(sourceDll, expectedChecksum: sha);

        (await act.Should().ThrowAsync<PluginVerificationException>())
            .Which.Message.Should()
            .Contain(PluginRefusalCode.ChecksumSubjectNotAPackage);
        File.Exists(InstalledPathFor(sourceDll)).Should().BeFalse();
    }

    [Fact]
    public async Task InstallPluginAsync_NoExpectedChecksum_SkipsVerificationAndCopies()
    {
        string sourceDll = WriteSourceDll([7, 7, 7]);

        await _manager.InstallPluginAsync(sourceDll);

        File.Exists(InstalledPathFor(sourceDll)).Should().BeTrue();
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_AbiMismatch_MarksMalfunctionedInsteadOfInstantiating()
    {
        Ulid pluginId = Ulid.NewUlid();
        string pluginDir = Path.Combine(_tempPluginsDir, "AbiMismatchPlugin");
        Directory.CreateDirectory(pluginDir);

        string dllPath = Path.Combine(pluginDir, "AbiMismatchPlugin.dll");
        await File.WriteAllBytesAsync(dllPath, [1, 2, 3]);

        string manifestJson =
            $@"{{
            ""id"": ""{pluginId}"",
            ""name"": ""AbiMismatchPlugin"",
            ""description"": ""A test"",
            ""version"": ""1.0.0"",
            ""assembly"": ""AbiMismatchPlugin.dll"",
            ""targetAbi"": ""12.0""
        }}";
        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        await _manager.LoadPluginFromManifestAsync(manifestPath);

        PluginInfo? info = _manager.GetInstalledPlugins().FirstOrDefault(p => p.Id == pluginId);

        info.Should().NotBeNull();
        info!.Status.Should().Be(PluginStatus.Malfunctioned);
        info.Verified.Should().BeFalse();
        _manager.GetPluginInstance(pluginId).Should().BeNull();
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
