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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Runtime;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Native code cannot be inspected or refused once it is mapped into the
/// process. The only moment to say no is before it loads, and the only thing
/// worth asking is who built the bundle: no capability the owner grants lifts
/// this one.
/// </summary>
public class PluginNativeTests : IDisposable
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000017");

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-native-{Ulid.NewUlid()}"
    );

    public PluginNativeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);

        GC.SuppressFinalize(this);
    }

    private static string FileName(string library)
    {
        if (OperatingSystem.IsWindows())
            return $"{library}.dll";

        return OperatingSystem.IsMacOS() ? $"lib{library}.dylib" : $"lib{library}.so";
    }

    private string Bundled(string library)
    {
        string path = Path.Combine(_folder, FileName(library));
        File.WriteAllText(path, "not really a library");

        return path;
    }

    private (PluginNative Native, RecordingLoader Loader) Native(bool signed)
    {
        RecordingLoader loader = new();

        return (new(Plugin, new Signature(signed), loader, _folder), loader);
    }

    [Fact]
    public async Task An_unsigned_bundle_refuses_and_nothing_is_mapped()
    {
        Bundled("libtorrent");
        (PluginNative native, RecordingLoader loader) = Native(signed: false);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            native.LoadAsync("libtorrent")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.NativeCodeUnsigned);
        loader
            .Loaded.Should()
            .BeEmpty("code that is already in the process cannot be refused afterwards");
    }

    [Fact]
    public async Task A_host_that_cannot_check_a_signature_refuses_rather_than_assuming()
    {
        Bundled("libtorrent");
        PluginNative native = new(Plugin, new NothingIsSigned(), new RecordingLoader(), _folder);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            native.LoadAsync("libtorrent")
        );

        refused
            .Refusal.Code.Should()
            .Be(
                PluginRefusalCodes.NativeCodeUnsigned,
                "not knowing a bundle is safe is not the same as knowing it is"
            );
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("nested/libtorrent")]
    [InlineData("nested\\libtorrent")]
    [InlineData("C:/Windows/System32/kernel32")]
    [InlineData("/usr/lib/libtorrent")]
    public async Task A_name_that_is_a_path_refuses_before_the_signature_is_even_asked(
        string library
    )
    {
        (PluginNative native, RecordingLoader loader) = Native(signed: true);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            native.LoadAsync(library)
        );

        refused
            .Refusal.Code.Should()
            .Be(
                PluginRefusalCodes.FileOutsideGrant,
                "the signature says the bundle was built by the marketplace and nothing about a file outside it"
            );
        loader.Loaded.Should().BeEmpty();
    }

    /// <summary>
    /// A separator refuses even when the file it names is really there, which
    /// is the only way to tell the guard from the missing-file check that
    /// happens to refuse with the same code.
    /// </summary>
    [Theory]
    [InlineData("nested/libtorrent")]
    [InlineData("nested\\libtorrent")]
    public async Task A_separator_refuses_even_when_that_file_exists(string library)
    {
        string nested = Path.Combine(_folder, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, FileName("libtorrent")), "not really a library");

        (PluginNative native, RecordingLoader loader) = Native(signed: true);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            native.LoadAsync(library)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
        loader.Loaded.Should().BeEmpty("a bundle is one folder, not a tree to walk");
    }

    [Fact]
    public async Task A_signed_bundle_loads_the_file_for_this_platform_from_its_own_folder()
    {
        string expected = Bundled("libtorrent");
        (PluginNative native, RecordingLoader loader) = Native(signed: true);

        await native.LoadAsync("libtorrent");

        loader.Loaded.Should().ContainSingle().Which.Should().Be(expected);
        native.IsLoaded("libtorrent").Should().BeTrue();
    }

    [Fact]
    public async Task A_library_the_bundle_does_not_carry_refuses()
    {
        (PluginNative native, RecordingLoader loader) = Native(signed: true);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            native.LoadAsync("missing")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
        loader.Loaded.Should().BeEmpty();
        native.IsLoaded("missing").Should().BeFalse();
    }

    [Fact]
    public async Task A_library_that_will_not_map_is_not_reported_as_loaded()
    {
        Bundled("broken");
        (PluginNative native, RecordingLoader loader) = Native(signed: true);
        loader.Succeed = false;

        await Assert.ThrowsAsync<PluginRefusedException>(() => native.LoadAsync("broken"));

        native
            .IsLoaded("broken")
            .Should()
            .BeFalse("a plugin told a library loaded calls into nothing");
    }

    [Fact]
    public async Task Loading_the_same_library_twice_maps_it_once()
    {
        Bundled("libtorrent");
        (PluginNative native, RecordingLoader loader) = Native(signed: true);

        await native.LoadAsync("libtorrent");
        await native.LoadAsync("libtorrent");

        loader.Loaded.Should().ContainSingle();
    }

    [Fact]
    public void Nothing_is_loaded_before_anything_asks()
    {
        (PluginNative native, _) = Native(signed: true);

        native.IsLoaded("libtorrent").Should().BeFalse();
    }

    private sealed class Signature(bool signed) : IPluginBundleSignature
    {
        public bool IsMarketplaceSigned(Ulid pluginId) => signed;
    }

    private sealed class RecordingLoader : INativeLibraryLoader
    {
        public List<string> Loaded { get; } = [];

        public bool Succeed { get; set; } = true;

        public bool TryLoad(string path, out nint handle)
        {
            handle = Succeed ? 1 : 0;

            if (Succeed)
                Loaded.Add(path);

            return Succeed;
        }
    }
}
