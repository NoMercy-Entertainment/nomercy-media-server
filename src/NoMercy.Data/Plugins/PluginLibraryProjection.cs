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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Encoder.Profiles;
using NoMercy.PluginSdk.Abstractions;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Every library with the presets an encode of its files runs, the set
/// <c>VideoEncodeJob</c> would run.
/// <para>
/// A preset counts when it is linked to any folder of the library, narrowed to
/// <c>Library.EncodePresetId</c> when that is set. Each preset is resolved
/// through <see cref="PresetResolver"/>, so an inherited ladder counts, and a
/// preset that does not resolve is left out rather than thrown into a plugin.
/// </para>
/// <para>
/// Flat reads and the shaping in memory: a GroupBy/First projection is the
/// SQLite APPLY trap this codebase avoids everywhere else.
/// </para>
/// </summary>
public sealed class PluginLibraryProjection(IDbContextFactory<MediaContext> contextFactory)
    : IPluginLibraryProjection
{
    // The resolver's own depth limit: a longer chain fails to resolve whatever
    // is loaded, so reading further buys nothing.
    private const int MaxInheritanceReads = 8;

    public async Task<List<PluginLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        var libraries = await context
            .Libraries.AsNoTracking()
            .Select(library => new
            {
                library.Id,
                library.Title,
                library.Type,
                library.EncodePresetId,
            })
            .ToListAsync(ct);

        var links = await context
            .FolderLibrary.AsNoTracking()
            .Join(
                context.EncodingPresetFolders.AsNoTracking(),
                folderLibrary => folderLibrary.FolderId,
                presetFolder => presetFolder.FolderId,
                (folderLibrary, presetFolder) =>
                    new
                    {
                        folderLibrary.LibraryId,
                        presetFolder.PresetId,
                        presetFolder.IsDefault,
                    }
            )
            .ToListAsync(ct);

        Dictionary<Ulid, PresetRow> presets = await LinkedPresetsAsync(
            context,
            links.Select(link => link.PresetId),
            ct
        );

        PresetRowLookup lookup = new(presets);
        Dictionary<Ulid, ResolvedPreset?> resolved = [];

        return
        [
            .. libraries.Select(library => new PluginLibrary(
                library.Id.ToString(),
                library.Title,
                library.Type
            )
            {
                EncodingPresets =
                [
                    .. links
                        .Where(link => link.LibraryId == library.Id)
                        .Where(link =>
                            library.EncodePresetId is null
                            || link.PresetId == library.EncodePresetId
                        )
                        .GroupBy(link => link.PresetId)
                        .Select(group =>
                            Describe(
                                group.Key,
                                group.Any(link => link.IsDefault),
                                presets,
                                lookup,
                                resolved
                            )
                        )
                        .OfType<PluginEncodingPreset>()
                        .OrderByDescending(preset => preset.IsDefault)
                        .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase),
                ],
            }),
        ];
    }

    /// <summary>
    /// The linked presets and the parents they inherit from, and nothing else:
    /// a server can hold many presets no folder uses, and every row read here
    /// is JSON resolved for each library listing. One read per inheritance
    /// level, which <see cref="MaxInheritanceReads"/> bounds the way the
    /// resolver bounds the chain itself.
    /// </summary>
    private static async Task<Dictionary<Ulid, PresetRow>> LinkedPresetsAsync(
        MediaContext context,
        IEnumerable<Ulid> linkedIds,
        CancellationToken ct
    )
    {
        Dictionary<Ulid, PresetRow> presets = [];
        List<Ulid> wanted = [.. linkedIds.Distinct()];

        for (int read = 0; read < MaxInheritanceReads && wanted.Count > 0; read++)
        {
            List<PresetRow> rows = await context
                .EncodingPresets.AsNoTracking()
                .Where(preset => wanted.Contains(preset.Id))
                .Select(preset => new PresetRow(
                    preset.Id,
                    preset.Name,
                    preset.ProfileJson,
                    preset.ParentPresetId
                ))
                .ToListAsync(ct);

            foreach (PresetRow row in rows)
                presets[row.Id] = row;

            wanted =
            [
                .. rows.Where(row =>
                        row.ParentPresetId is { } parent && !presets.ContainsKey(parent)
                    )
                    .Select(row => row.ParentPresetId!.Value)
                    .Distinct(),
            ];
        }

        return presets;
    }

    /// <summary>
    /// The tallest video a profile can produce before any file is seen.
    /// <list type="bullet">
    ///   <item>A Manual ladder, or an Auto one carrying several rungs (which
    ///   the planner runs as Manual): its highest rung.</item>
    ///   <item>An Auto ladder with tiers: its highest tier, since
    ///   <c>NeverUpscale</c> only ever takes rungs away from the top.</item>
    ///   <item>An Auto ladder without tiers follows the source, so null.</item>
    ///   <item>No ladder: <c>Video.Height</c>, null when it keeps the source's
    ///   size or there is no video.</item>
    /// </list>
    /// </summary>
    private static int? MaxHeightOf(EncodingProfile profile)
    {
        int? videoHeight = profile.Video?.Height is int height and > 0 ? height : null;

        if (profile.Ladder is not { } ladder)
            return videoHeight;

        LadderRung[] rungs = ladder.Rungs ?? [];

        if (ladder.Mode == LadderMode.Manual || rungs.Length > 1)
            return rungs.Length > 0 ? rungs.Max(rung => rung.Height) : videoHeight;

        bool hasReference = profile.Video is not null || rungs.Length == 1;
        if (!hasReference)
            return null;

        return ladder.AutoConfig is { Tiers.Length: > 0 } auto
            ? auto.Tiers.Max(tier => tier.Height)
            : null;
    }

    private static PluginEncodingPreset? Describe(
        Ulid presetId,
        bool isDefault,
        Dictionary<Ulid, PresetRow> presets,
        PresetRowLookup lookup,
        Dictionary<Ulid, ResolvedPreset?> resolved
    )
    {
        if (!resolved.TryGetValue(presetId, out ResolvedPreset? preset))
        {
            preset = Resolve(presetId, presets, lookup);
            resolved[presetId] = preset;
        }

        return preset is null
            ? null
            : new PluginEncodingPreset(
                presetId.ToString(),
                preset.Name,
                isDefault,
                preset.MaxHeight
            );
    }

    private static ResolvedPreset? Resolve(
        Ulid presetId,
        Dictionary<Ulid, PresetRow> presets,
        PresetRowLookup lookup
    )
    {
        if (!presets.TryGetValue(presetId, out PresetRow? row))
            return null;

        try
        {
            EncodingProfile profile = PresetResolver.Resolve(presetId, lookup);
            return new(row.Name, MaxHeightOf(profile));
        }
        catch (Exception)
        {
            // A broken preset (bad JSON, a cycle, a missing parent) is one the
            // encoder skips too. Leaving it out keeps it from failing the whole
            // library read for a plugin that cannot do anything about it.
            return null;
        }
    }

    private sealed record PresetRow(Ulid Id, string Name, string ProfileJson, Ulid? ParentPresetId);

    private sealed record ResolvedPreset(string Name, int? MaxHeight);

    private sealed class PresetRowLookup(Dictionary<Ulid, PresetRow> presets) : IPresetLookup
    {
        public (string ProfileJson, Ulid? ParentPresetId)? Get(Ulid presetId) =>
            presets.TryGetValue(presetId, out PresetRow? row)
                ? (row.ProfileJson, row.ParentPresetId)
                : null;
    }
}
