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

using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Hooks;

/// <summary>
/// Asks every active <see cref="IMetadataPlugin"/> that declares the
/// <c>metadata</c> capability what it knows about a title, and merges the
/// answers field by field: the first plugin to fill a field keeps it.
/// <para>
/// A plugin that throws or hangs past <see cref="PerPluginTimeout"/> contributes
/// nothing and is logged. Metadata is fetched inside an import job, and an
/// import that stops because a plugin stopped is a library that stops.
/// </para>
/// </summary>
public class PluginMetadataResolver(
    IPluginManager pluginManager,
    ILogger<PluginMetadataResolver> logger
) : IPluginMetadataResolver
{
    public TimeSpan PerPluginTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<MediaMetadata?> ResolveAsync(
        string title,
        MediaType type,
        CancellationToken ct = default
    )
    {
        MediaMetadata? merged = null;

        foreach (IMetadataPlugin plugin in pluginManager.GetPluginsOfType<IMetadataPlugin>())
        {
            PluginCapabilities? capabilities = pluginManager.GetPluginInfo(plugin.Id)?.Capabilities;

            if (!PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.Metadata))
                continue;

            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerPluginTimeout);

            try
            {
                MediaMetadata? answer = await plugin.GetMetadataAsync(
                    title,
                    type,
                    timeoutCts.Token
                );

                if (answer is null)
                    continue;

                merged = merged is null ? answer : Fill(merged, answer);
            }
            catch (Exception ex)
            {
                if (PluginStaleMemberLog.Explain(logger, plugin.Id, ex, "answering for metadata"))
                    continue;

                logger.LogWarning(
                    ex,
                    "Metadata plugin {Plugin} failed or timed out for {Title}; its answer is skipped.",
                    plugin.Id,
                    title
                );
            }
        }

        return merged;
    }

    /// <summary>
    /// Fields the first answer left empty, taken from a later one. Whoever
    /// answered first keeps every field they filled, so the merge does not
    /// depend on load order for anything already known.
    /// </summary>
    private static MediaMetadata Fill(MediaMetadata first, MediaMetadata next) =>
        new()
        {
            Title = string.IsNullOrWhiteSpace(first.Title) ? next.Title : first.Title,
            Overview = first.Overview ?? next.Overview,
            Year = first.Year ?? next.Year,
            PosterUrl = first.PosterUrl ?? next.PosterUrl,
            BackdropUrl = first.BackdropUrl ?? next.BackdropUrl,
            Genres = first.Genres.Count > 0 ? first.Genres : next.Genres,
            Rating = first.Rating ?? next.Rating,
            ExternalId = first.ExternalId ?? next.ExternalId,
            ExternalSource = first.ExternalSource ?? next.ExternalSource,
        };
}
