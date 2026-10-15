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
using NoMercy.PluginSdk.Quotas;
using NoMercy.PluginSdk.Storage;
using NoMercy.Storage.Drivers.Local;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin marks a file it just created sparse through its storage scope,
/// so it never carries the native call the marketplace scan refuses. The
/// scope's guard decides which file, the host decides how.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginStorageSparseTests : IDisposable
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000016");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-sparse-{Ulid.NewUlid()}"
    );

    public PluginStorageSparseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_file_inside_the_scope_is_handed_to_the_host_by_its_full_path()
    {
        List<string> marked = [];
        PluginLocalStorageScope scope = new(
            Plugin,
            _root,
            path =>
            {
                marked.Add(path);
                return true;
            }
        );
        File.WriteAllBytes(Path.Combine(_root, "piece.bin"), []);

        bool result = await scope.MarkSparseAsync("piece.bin");

        result.Should().BeTrue();
        marked.Should().Equal(Path.Combine(_root, "piece.bin"));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("nested/../../outside.bin")]
    public async Task A_path_out_of_the_scope_is_refused_and_nothing_is_marked(string path)
    {
        bool called = false;
        PluginLocalStorageScope scope = new(
            Plugin,
            _root,
            _ =>
            {
                called = true;
                return true;
            }
        );

        Func<Task> mark = () => scope.MarkSparseAsync(path);

        await mark.Should().ThrowAsync<PluginRefusedException>();
        called.Should().BeFalse();
    }

    [Fact]
    public async Task An_absolute_path_is_refused()
    {
        PluginLocalStorageScope scope = new(Plugin, _root, _ => true);

        Func<Task> mark = () => scope.MarkSparseAsync(Path.Combine(_root, "piece.bin"));

        await mark.Should().ThrowAsync<PluginRefusedException>();
    }

    [Fact]
    public async Task A_file_that_does_not_exist_is_not_handed_to_the_host()
    {
        bool called = false;
        PluginLocalStorageScope scope = new(
            Plugin,
            _root,
            _ =>
            {
                called = true;
                return true;
            }
        );

        (await scope.MarkSparseAsync("missing.bin")).Should().BeFalse();
        called.Should().BeFalse();
    }

    /// <summary>A scope a host built without the native call answers false, never throws.</summary>
    [Fact]
    public async Task A_scope_without_the_hosts_native_call_answers_false()
    {
        PluginLocalStorageScope scope = new(Plugin, _root);
        File.WriteAllBytes(Path.Combine(_root, "piece.bin"), []);

        (await scope.MarkSparseAsync("piece.bin")).Should().BeFalse();
    }

    /// <summary>A scope written before 12.3 gets the default: nothing done, nothing thrown.</summary>
    [Fact]
    public async Task A_scope_that_does_not_implement_it_answers_false()
    {
        IPluginStorageScope scope = new OlderScope();

        (await scope.MarkSparseAsync("piece.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Every_scope_of_the_plugins_own_storage_reaches_the_host()
    {
        List<string> marked = [];
        PluginHostStorage storage = new(
            Plugin,
            _root,
            new NoFolders(),
            new PluginHostStorageTests.StubGrants(Plugin),
            new PluginQuotaMeter(new Unlimited(), TimeProvider.System),
            markSparse: path =>
            {
                marked.Add(path);
                return true;
            }
        );

        foreach (
            IPluginStorageScope scope in new[] { storage.Private, storage.Temp, storage.Derived }
        )
        {
            await using (await scope.OpenWriteAsync("piece.bin", overwrite: true)) { }

            (await scope.MarkSparseAsync("piece.bin")).Should().BeTrue();
        }

        marked.Should().HaveCount(3, "the private folder is metered, and metering passes it on");
    }

    /// <summary>
    /// End to end on the platform that needs it: the file the plugin just
    /// created, still open for writing, comes out sparse.
    /// </summary>
    [SkippableFact]
    public async Task On_windows_the_file_the_plugin_is_writing_comes_out_sparse()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only NTFS needs the flag set.");
        PluginLocalStorageScope scope = new(Plugin, _root, new LocalStorageDriver().TryMarkSparse);

        await using Stream writer = await scope.OpenWriteAsync("download.bin", overwrite: true);

        (await scope.MarkSparseAsync("download.bin")).Should().BeTrue();
        File.GetAttributes(Path.Combine(_root, "download.bin"))
            .Should()
            .HaveFlag(FileAttributes.SparseFile);
    }

    /// <summary>
    /// A torrent client's incomplete downloads live in a folder the owner set
    /// in the plugin's settings, which is no scope. The plugin's storage marks
    /// a file there by its full local path.
    /// </summary>
    [Fact]
    public async Task A_file_at_a_local_path_outside_every_scope_reaches_the_host()
    {
        List<string> marked = [];
        PluginHostStorage storage = Storage(path =>
        {
            marked.Add(path);
            return true;
        });
        string incomplete = Path.Combine(_root, "elsewhere", "incomplete", "movie.mkv");

        (await storage.MarkSparseAsync(incomplete)).Should().BeTrue();

        marked.Should().Equal(incomplete);
    }

    [Fact]
    public async Task A_storage_built_without_the_hosts_native_call_answers_false()
    {
        (await Storage(null).MarkSparseAsync(Path.Combine(_root, "movie.mkv"))).Should().BeFalse();
    }

    /// <summary>A storage written before 12.4 gets the default: nothing done, nothing thrown.</summary>
    [Fact]
    public async Task A_storage_that_does_not_implement_it_answers_false()
    {
        IPluginStorage storage = new OlderStorage();

        (await storage.MarkSparseAsync("/downloads/movie.mkv")).Should().BeFalse();
    }

    /// <summary>
    /// End to end on Windows: a file the plugin is writing, in a folder that
    /// is no scope, comes out sparse through the same native call the scopes
    /// use, while the writer still holds it open.
    /// </summary>
    [SkippableFact]
    public async Task On_windows_a_download_outside_every_scope_comes_out_sparse()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only NTFS needs the flag set.");
        string folder = Path.Combine(_root, "incomplete");
        Directory.CreateDirectory(folder);
        string download = Path.Combine(folder, "movie.mkv");
        PluginHostStorage storage = Storage(new LocalStorageDriver().TryMarkSparse);

        await using FileStream writer = new(
            download,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read
        );

        (await storage.MarkSparseAsync(download)).Should().BeTrue();
        File.GetAttributes(download).Should().HaveFlag(FileAttributes.SparseFile);
    }

    [SkippableFact]
    public async Task On_windows_a_relative_path_is_not_marked()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only NTFS needs the flag set.");

        (await Storage(new LocalStorageDriver().TryMarkSparse).MarkSparseAsync("movie.mkv"))
            .Should()
            .BeFalse();
    }

    private PluginHostStorage Storage(Func<string, bool>? markSparse) =>
        new(
            Plugin,
            _root,
            new NoFolders(),
            new PluginHostStorageTests.StubGrants(Plugin),
            markSparse: markSparse
        );

    private sealed class OlderStorage : IPluginStorage
    {
        public IPluginStorageScope Private => throw new NotSupportedException();

        public IPluginStorageScope Temp => throw new NotSupportedException();

        public IPluginStorageScope Derived => throw new NotSupportedException();

        public IPluginUserScope ForUser => throw new NotSupportedException();

        public Task<IPluginStorageScope> PathAsync(
            string folderId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<IPluginDatabase> OpenDatabaseAsync(
            string name,
            CancellationToken ct = default
        ) => throw new NotSupportedException();
    }

    private sealed class Unlimited : IPluginQuotaSource
    {
        public PluginQuota For(Ulid pluginId) =>
            new(50, 512 * 1024 * 1024, long.MaxValue, long.MaxValue);
    }

    private sealed class NoFolders : IPluginFolderCatalog
    {
        public Task<IReadOnlyList<PluginStorageLocation>> LocationsAsync(
            CancellationToken ct = default
        ) => Task.FromResult<IReadOnlyList<PluginStorageLocation>>([]);

        public Task<IPluginStorageScope?> OpenAsync(
            string locationId,
            CancellationToken ct = default
        ) => Task.FromResult<IPluginStorageScope?>(null);
    }

    private sealed class OlderScope : IPluginStorageScope
    {
        public PluginStorageLocation? Location => null;

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(
            string path,
            bool overwrite,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public IAsyncEnumerable<PluginStorageEntry> ListAsync(
            string path,
            bool recursive = false,
            CancellationToken ct = default
        ) => AsyncEnumerable.Empty<PluginStorageEntry>();
    }
}
