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
using NoMercy.PluginSdk.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Facts about the server, so a plugin branches on one rather than reading
/// the process and guessing. Free space is refused before it is measured for
/// a folder nobody granted: how full somebody's disk is, is a fact about
/// their machine.
/// </summary>
public class PluginServerInfoTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000016");
    private const string Movies = "01J9ZK5V8Y000000000000AAAA";
    private const string Downloads = "01J9ZK5V8Y000000000000BBBB";

    private static PluginServerInfo Info(long freeBytes, params string[] granted)
    {
        PluginHostStorageTests.StubGrants grants = new(Plugin, granted);
        PluginGrantedLocations locations = new(new StubCatalog(Movies, Downloads), grants);

        locations.RefreshAsync(Plugin).GetAwaiter().GetResult();

        return new(Plugin, new(1, 2, 3), locations, new StubProbe(freeBytes), grants);
    }

    [Fact]
    public void The_platform_is_one_of_the_three_words_the_contract_names()
    {
        Info(0).Platform.Should().BeOneOf("windows", "linux", "macos");
    }

    [Fact]
    public void The_version_is_the_servers_own()
    {
        Info(0).Version.Should().Be(new Version(1, 2, 3));
    }

    [Fact]
    public void Granted_paths_lists_only_what_the_owner_granted()
    {
        Info(0, Movies).GrantedPaths.Should().ContainSingle().Which.Id.Should().Be(Movies);
    }

    [Fact]
    public void Granted_paths_is_empty_when_the_owner_granted_none()
    {
        Info(0)
            .GrantedPaths.Should()
            .BeEmpty(
                "empty is a plugin's cue to ask, and a wrong list is a plugin writing nowhere"
            );
    }

    [Fact]
    public async Task Free_space_on_a_granted_folder_is_what_the_probe_measured()
    {
        (await Info(4_294_967_296, Movies).FreeSpaceBytesAsync(Movies)).Should().Be(4_294_967_296);
    }

    [Fact]
    public async Task Free_space_on_a_driver_that_cannot_be_measured_answers_minus_one()
    {
        (await Info(-1, Movies).FreeSpaceBytesAsync(Movies))
            .Should()
            .Be(-1, "a guessed number is a plugin writing until the write fails");
    }

    [Fact]
    public async Task Free_space_on_an_ungranted_folder_refuses_before_it_measures()
    {
        StubProbe probe = new(4_294_967_296);
        PluginHostStorageTests.StubGrants grants = new(Plugin);
        PluginGrantedLocations locations = new(new StubCatalog(Movies), grants);
        PluginServerInfo info = new(Plugin, new(1, 0), locations, probe, grants);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            info.FreeSpaceBytesAsync(Downloads)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
        probe.Asked.Should().BeEmpty("how full somebody's disk is, is a fact about their machine");
    }

    private sealed class StubProbe(long answer) : IPluginFreeSpaceProbe
    {
        public List<string> Asked { get; } = [];

        public Task<long> FreeBytesAsync(string folderId, CancellationToken ct = default)
        {
            Asked.Add(folderId);

            return Task.FromResult(answer);
        }
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
        ) => Task.FromResult<IPluginStorageScope?>(null);
    }
}
