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
using NoMercy.Plugins.Access;

namespace NoMercy.Plugins.Search;

/// <summary>Asks every plugin the caller may use, and waits only so long.</summary>
public interface IPluginSearchService
{
    Task<IReadOnlyList<PluginSearchGroup>> SearchAsync(
        string query,
        PluginCaller caller,
        TimeSpan deadline,
        CancellationToken ct
    );
}

/// <summary>
/// Global search, answered by the plugins as well as by the library.
///
/// <para>
/// Every plugin is asked at once and the host stops waiting when its deadline
/// passes: one slow provider was one slow search box for everyone, including
/// the library results that were ready immediately. A plugin that is late, or
/// that throws, is simply absent from the answer.
/// </para>
/// </summary>
public class PluginSearchService(
    IPluginManager pluginManager,
    IPluginAccessResolver accessResolver,
    ILogger<PluginSearchService> logger
) : IPluginSearchService
{
    public async Task<IReadOnlyList<PluginSearchGroup>> SearchAsync(
        string query,
        PluginCaller caller,
        TimeSpan deadline,
        CancellationToken ct
    )
    {
        // An empty box is not a search. Asking anyway woke every provider on
        // every keystroke that cleared the field.
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(deadline);

        IEnumerable<Task<PluginSearchGroup?>> asked = pluginManager
            .GetInstalledPlugins()
            .Where(info => info.Status == PluginStatus.Active)
            .Where(info =>
                accessResolver.Resolve(info.Id, caller.Id.Value.ToGuid()) != PluginAccess.None
            )
            .Select(info => (info, plugin: pluginManager.GetPluginInstance(info.Id)))
            .Where(pair => pair.plugin is ISearchablePlugin)
            .Select(pair =>
                AskAsync(pair.info, (ISearchablePlugin)pair.plugin!, query, caller, timer.Token)
            );

        PluginSearchGroup?[] answers = await Task.WhenAll(asked);

        return [.. answers.Where(group => group is { Results.Count: > 0 }).Select(group => group!)];
    }

    private async Task<PluginSearchGroup?> AskAsync(
        PluginInfo info,
        ISearchablePlugin plugin,
        string query,
        PluginCaller caller,
        CancellationToken ct
    )
    {
        try
        {
            IReadOnlyList<PluginSearchResult> results = await plugin.SearchAsync(query, caller, ct);

            return new PluginSearchGroup(info.Id.ToString(), info.Name, results);
        }
        catch (OperationCanceledException)
        {
            // Late rather than broken. Logged at a level nobody is paged for,
            // because a provider being slow once is a network, not a fault.
            logger.LogDebug("Plugin {Plugin} did not answer the search in time", info.Name);

            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {Plugin} failed to answer a search", info.Name);

            return null;
        }
    }
}
