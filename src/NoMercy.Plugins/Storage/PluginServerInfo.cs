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

using System.Collections.Concurrent;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.Storage;

/// <summary>
/// The granted folders, cached.
/// <para>
/// Cached because a plugin reads <see cref="IPluginServerInfo.GrantedPaths" />
/// in a loop and the answer lives in a database. A property that queried
/// would turn a plugin's own loop into the server's slowest one.
/// </para>
/// </summary>
public interface IPluginGrantedLocations
{
    IReadOnlyList<PluginStorageLocation> For(Ulid pluginId);

    Task RefreshAsync(Ulid pluginId, CancellationToken ct = default);
}

/// <inheritdoc />
/// <param name="catalog">
/// Null on a host that wired no folder catalogue, where the honest answer is
/// that the owner granted nothing rather than a list nobody could open.
/// </param>
public class PluginGrantedLocations(IPluginFolderCatalog? catalog, IPluginGrantStore grants)
    : IPluginGrantedLocations
{
    private readonly ConcurrentDictionary<Ulid, IReadOnlyList<PluginStorageLocation>> _cache =
        new();

    public IReadOnlyList<PluginStorageLocation> For(Ulid pluginId) =>
        _cache.TryGetValue(pluginId, out IReadOnlyList<PluginStorageLocation>? cached)
            ? cached
            : [];

    public async Task RefreshAsync(Ulid pluginId, CancellationToken ct = default)
    {
        if (catalog is null)
            return;

        string kind = PluginGrantKind.ForCapability(PluginCapabilityNames.StoragePath);
        IReadOnlyList<PluginStorageLocation> all = await catalog.LocationsAsync(ct);

        _cache[pluginId] = [.. all.Where(location => grants.Holds(pluginId, kind, location.Id))];
    }
}

/// <summary>
/// Free space on one of the server's folders.
/// <para>
/// Minus one means this driver cannot be measured, which a plugin can act on.
/// A guessed number cannot: a plugin told there is room writes until the
/// write fails, which is later and worse.
/// </para>
/// </summary>
public interface IPluginFreeSpaceProbe
{
    Task<long> FreeBytesAsync(string folderId, CancellationToken ct = default);
}

/// <summary>What this server is, so a plugin branches on a fact rather than a guess.</summary>
public class PluginServerInfo(
    Ulid pluginId,
    Version version,
    IPluginGrantedLocations locations,
    IPluginFreeSpaceProbe freeSpace,
    IPluginGrantStore grants
) : IPluginServerInfo
{
    public Version Version { get; } = version;

    public string Platform
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return "windows";

            if (OperatingSystem.IsMacOS())
                return "macos";

            return "linux";
        }
    }

    public IReadOnlyList<PluginStorageLocation> GrantedPaths => locations.For(pluginId);

    public Task<long> FreeSpaceBytesAsync(string folderId, CancellationToken ct = default)
    {
        string kind = PluginGrantKind.ForCapability(PluginCapabilityNames.StoragePath);

        // Refused before it measures. Measuring first would tell a plugin how
        // full a disk it was never granted is, which is a fact about the
        // owner's machine it has no business with.
        if (!grants.Holds(pluginId, kind, folderId))
            throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), folderId)
            );

        return freeSpace.FreeBytesAsync(folderId, ct);
    }
}
