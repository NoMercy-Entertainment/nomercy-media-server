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
using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using MediaFile = NoMercy.NmSystem.Dto.MediaFile;
using MediaFolderExtend = NoMercy.NmSystem.Dto.MediaFolderExtend;
using PluginMediaFile = NoMercy.Plugins.Abstractions.MediaFile;

namespace NoMercy.Plugins.Hooks;

/// <summary>
/// Asks every active <see cref="IMediaSourcePlugin"/> that declares the
/// <c>mediaSource</c> capability what it found under a path, and hands the answer
/// back in the shape the scan already works in.
/// <para>
/// A scan is a long job that must finish. A plugin that throws or hangs past
/// <see cref="PerPluginTimeout"/> contributes nothing and is logged; it never
/// takes the library scan down with it.
/// </para>
/// </summary>
public class PluginMediaSourceProvider(
    IPluginManager pluginManager,
    ILogger<PluginMediaSourceProvider> logger
) : IPluginMediaSourceProvider
{
    public TimeSpan PerPluginTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<MediaFolderExtend>> ScanAsync(
        string path,
        CancellationToken ct = default
    )
    {
        List<MediaFolderExtend> found = [];

        foreach (IMediaSourcePlugin plugin in pluginManager.GetPluginsOfType<IMediaSourcePlugin>())
        {
            PluginCapabilities? capabilities = pluginManager.GetPluginInfo(plugin.Id)?.Capabilities;

            if (!PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.MediaSource))
                continue;

            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerPluginTimeout);

            try
            {
                IEnumerable<PluginMediaFile> files = await plugin.ScanAsync(path, timeoutCts.Token);

                MediaFolderExtend? folder = FolderOf(path, files);
                if (folder is not null)
                    found.Add(folder);
            }
            catch (Exception ex)
            {
                if (PluginStaleMemberLog.Explain(logger, plugin.Id, ex, "offering a media source"))
                    continue;

                logger.LogWarning(
                    ex,
                    "Media source plugin {Plugin} failed or timed out scanning {Path}; its files are skipped.",
                    plugin.Id,
                    path
                );
            }
        }

        return found;
    }

    /// <summary>
    /// One folder holding what a plugin found, or nothing when it found nothing.
    /// <para>
    /// Parsed, probed and tagged are all left unset on purpose: the scan fills
    /// them from the file on disk with the same parser it uses for its own
    /// finds. Setting them here would be this class inventing what a file means,
    /// which is the one thing a plugin must not be able to do.
    /// </para>
    /// </summary>
    private static MediaFolderExtend? FolderOf(string path, IEnumerable<PluginMediaFile> files)
    {
        ConcurrentBag<MediaFile> converted = [];

        foreach (PluginMediaFile file in files)
            converted.Add(
                new MediaFile
                {
                    Name = file.FileName,
                    Path = file.Path,
                    Extension = Path.GetExtension(file.Path),
                    Size = file.Size,
                    Type = "file",
                }
            );

        if (converted.IsEmpty)
            return null;

        return new MediaFolderExtend
        {
            Name = Path.GetFileName(path.TrimEnd('/', '\\')),
            Path = path,
            Type = "folder",
            Files = converted,
        };
    }
}
