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

using System.Runtime.CompilerServices;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Quotas;
using NoMercy.PluginSdk.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// An absolute path and a `..` segment are the two ways out of a scope, and
/// both refuse. A path that resolved somewhere else would be a plugin reading
/// the server's own files while every line of it looked like storage code.
/// </summary>
public class PluginHostStorageTests : IDisposable
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000015");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-storage-{Ulid.NewUlid()}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private PluginHostStorage Storage(
        IPluginFolderCatalog? catalog = null,
        params string[] grantedFolders
    ) => new(Plugin, _root, catalog ?? new StubCatalog(), new StubGrants(Plugin, grantedFolders));

    private static async Task WriteAsync(IPluginStorageScope scope, string path, string text)
    {
        await using Stream stream = await scope.OpenWriteAsync(path, true);
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(text);
    }

    private static async Task<string> ReadAsync(IPluginStorageScope scope, string path)
    {
        await using Stream stream = await scope.OpenReadAsync(path);
        using StreamReader reader = new(stream);

        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task A_private_file_is_read_back_from_private_and_is_not_in_temp()
    {
        PluginHostStorage storage = Storage();

        await WriteAsync(storage.Private, "state.json", "{}");

        (await ReadAsync(storage.Private, "state.json")).Should().Be("{}");
        (await storage.Temp.ExistsAsync("state.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Temp_is_emptied_when_the_facade_is_built()
    {
        await WriteAsync(Storage().Temp, "half-done.part", "x");

        (await Storage().Temp.ExistsAsync("half-done.part"))
            .Should()
            .BeFalse("a folder called temp that survives restarts grows until a disk fills");
    }

    [Fact]
    public async Task A_restart_leaves_private_alone()
    {
        await WriteAsync(Storage().Private, "state.json", "{}");

        (await Storage().Private.ExistsAsync("state.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Derived_is_its_own_place()
    {
        PluginHostStorage storage = Storage();

        await WriteAsync(storage.Derived, "stem.flac", "audio");

        (await storage.Private.ExistsAsync("stem.flac")).Should().BeFalse();
    }

    [Theory]
    [InlineData("../escaped.json")]
    [InlineData("nested/../../escaped.json")]
    [InlineData("..\\escaped.json")]
    public async Task A_path_that_climbs_out_of_the_scope_refuses(string path)
    {
        PluginHostStorage storage = Storage();

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            WriteAsync(storage.Private, path, "x")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
    }

    [Fact]
    public async Task An_absolute_path_refuses()
    {
        PluginHostStorage storage = Storage();

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            WriteAsync(storage.Private, Path.Combine(Path.GetTempPath(), "escaped"), "x")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
    }

    [Fact]
    public async Task A_nested_path_inside_the_scope_is_fine()
    {
        PluginHostStorage storage = Storage();

        await WriteAsync(storage.Private, "pieces/0/data.bin", "x");

        (await storage.Private.ExistsAsync("pieces/0/data.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task An_ungranted_folder_id_refuses_and_names_it()
    {
        PluginHostStorage storage = Storage();

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            storage.PathAsync("01J9ZK5V8Y000000000000FFFF")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
        refused.Refusal.What.Should().Contain("01J9ZK5V8Y000000000000FFFF");
    }

    [Fact]
    public async Task A_granted_folder_id_opens_the_folder_the_catalog_owns()
    {
        StubCatalog catalog = new("movies");
        PluginHostStorage storage = Storage(catalog, "movies");

        IPluginStorageScope scope = await storage.PathAsync("movies");

        scope.Location!.Id.Should().Be("movies");
    }

    [Fact]
    public async Task A_granted_folder_the_server_no_longer_has_refuses_rather_than_answering_empty()
    {
        PluginHostStorage storage = Storage(new StubCatalog(), "movies");

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            storage.PathAsync("movies")
        );

        refused
            .Refusal.Code.Should()
            .Be(
                PluginRefusalCodes.FileOutsideGrant,
                "an empty scope is one a plugin writes into and finds gone"
            );
    }

    [Fact]
    public async Task Listing_answers_paths_that_can_be_handed_back_to_it()
    {
        PluginHostStorage storage = Storage();
        await WriteAsync(storage.Private, "pieces/0/data.bin", "x");

        List<PluginStorageEntry> entries = [];

        await foreach (PluginStorageEntry entry in storage.Private.ListAsync("", recursive: true))
            entries.Add(entry);

        string file = entries.Single(entry => !entry.IsDirectory).Path;

        file.Should().Be("pieces/0/data.bin");
        (await storage.Private.ExistsAsync(file)).Should().BeTrue();
    }

    [Fact]
    public async Task Removing_something_removes_it()
    {
        PluginHostStorage storage = Storage();
        await WriteAsync(storage.Private, "state.json", "{}");

        await storage.Private.DeleteAsync("state.json");

        (await storage.Private.ExistsAsync("state.json")).Should().BeFalse();
    }

    [Fact]
    public async Task A_write_to_the_private_folder_counts_against_the_disk_allowance()
    {
        PluginQuotaMeter meter = new(new StubQuotas(10 * 1024), TimeProvider.System);
        PluginHostStorage storage = new(
            Plugin,
            _root,
            new StubCatalog(),
            new StubGrants(Plugin),
            meter
        );

        await WriteAsync(storage.Private, "state.json", new string('x', 4096));

        meter.DiskUsed(Plugin).Should().Be(4096);
    }

    [Fact]
    public async Task A_write_past_the_disk_allowance_is_refused_before_the_bytes_land()
    {
        PluginQuotaMeter meter = new(new StubQuotas(1024), TimeProvider.System);
        PluginHostStorage storage = new(
            Plugin,
            _root,
            new StubCatalog(),
            new StubGrants(Plugin),
            meter
        );

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            WriteAsync(storage.Private, "big.bin", new string('x', 4096))
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.QuotaDiskExceeded);
    }

    [Fact]
    public async Task Removing_a_file_gives_the_room_back()
    {
        PluginQuotaMeter meter = new(new StubQuotas(10 * 1024), TimeProvider.System);
        PluginHostStorage storage = new(
            Plugin,
            _root,
            new StubCatalog(),
            new StubGrants(Plugin),
            meter
        );
        await WriteAsync(storage.Private, "state.json", new string('x', 4096));

        await storage.Private.DeleteAsync("state.json");

        meter
            .DiskUsed(Plugin)
            .Should()
            .Be(0, "a plugin that tidies up and stays over its allowance would never recover");
    }

    [Fact]
    public async Task Temp_is_not_counted_against_the_plugin()
    {
        PluginQuotaMeter meter = new(new StubQuotas(10 * 1024), TimeProvider.System);
        PluginHostStorage storage = new(
            Plugin,
            _root,
            new StubCatalog(),
            new StubGrants(Plugin),
            meter
        );

        await WriteAsync(storage.Temp, "scratch.bin", new string('x', 4096));

        meter
            .DiskUsed(Plugin)
            .Should()
            .Be(0, "temp is emptied by the server, so charging the plugin for it charges it twice");
    }

    private sealed class StubQuotas(long diskBytes) : IPluginQuotaSource
    {
        public PluginQuota For(Ulid pluginId) =>
            new(50, 512 * 1024 * 1024, diskBytes, long.MaxValue);
    }

    private sealed class StubCatalog(params string[] known) : IPluginFolderCatalog
    {
        public Task<IReadOnlyList<PluginStorageLocation>> LocationsAsync(
            CancellationToken ct = default
        ) =>
            Task.FromResult<IReadOnlyList<PluginStorageLocation>>([
                .. known.Select(id => new PluginStorageLocation(id, id, "local", true)),
            ]);

        public Task<IPluginStorageScope?> OpenAsync(
            string locationId,
            CancellationToken ct = default
        ) =>
            Task.FromResult<IPluginStorageScope?>(
                known.Contains(locationId)
                    ? new StubScope(new(locationId, locationId, "local", true))
                    : null
            );
    }

    private sealed class StubScope(PluginStorageLocation location) : IPluginStorageScope
    {
        public PluginStorageLocation? Location => location;

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<Stream> OpenWriteAsync(
            string path,
            bool overwrite,
            CancellationToken ct = default
        ) => Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<PluginStorageEntry> ListAsync(
            string path,
            bool recursive = false,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            yield break;
        }
    }

    internal sealed class StubGrants(Ulid pluginId, params string[] folders) : IPluginGrantStore
    {
        private readonly string _kind = PluginGrantKind.ForCapability(
            PluginCapabilityNames.StoragePath
        );

        public IReadOnlyList<string> Granted(Ulid id, string kind) =>
            id == pluginId && kind == _kind ? folders : [];

        public bool Holds(Ulid id, string kind, string value) =>
            id == pluginId && kind == _kind && folders.Contains(value);

        public void Grant(Ulid id, string kind, string value) { }

        public void Revoke(Ulid id, string kind, string value) { }

        public void RevokeAll(Ulid id) { }

        public void Request(Ulid id, string kind, string value, string reason) { }

        public IReadOnlyList<PluginGrantRequest> PendingRequests() => [];

        public void ClearRequest(Ulid id, string kind, string value) { }
    }
}
