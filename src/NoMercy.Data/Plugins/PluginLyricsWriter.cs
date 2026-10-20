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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NoMercy.Data.Services.Music;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The host side of <see cref="IPluginLyrics" />. Every rule a plugin's write
/// has to pass is enforced here, not trusted to the plugin: synced provider
/// lyrics are never overwritten, a write lands only at the version it read,
/// the lines must be a timeline the player can follow, and the text keeps a
/// source the client can credit.
/// <para>
/// A refusal comes back as a <see cref="PluginWriteResult" /> whose reason
/// starts with a <see cref="PluginLyricsRefusal" /> code, never as an
/// exception: a plugin's sweep runs unattended. What nobody planned for - a
/// locked database, a disk that went away - is caught by the call guard,
/// logged and refused with the exception's type name. Cancellation the caller
/// asked for still propagates.
/// </para>
/// </summary>
public class PluginLyricsWriter(
    Ulid pluginId,
    IDbContextFactory<MediaContext> contextFactory,
    ILogger<PluginLyricsWriter>? logger = null
) : IPluginLyrics
{
    // A word is "where its line starts" within a millisecond: alignment
    // produces floating point, and a plugin that rounds differently from the
    // host is not writing a wrong timeline.
    private const double TimeTolerance = 0.001;

    private static readonly HashSet<string> KnownSources = new(StringComparer.Ordinal)
    {
        LyricsSources.Lrclib,
        LyricsSources.Musixmatch,
        LyricsSources.Embedded,
        LyricsSources.Transcribed,
    };

    private readonly ILogger<PluginLyricsWriter> _logger =
        logger ?? NullLogger<PluginLyricsWriter>.Instance;

    public Task<PluginLyricsState?> GetStateAsync(Guid trackId, CancellationToken ct = default) =>
        PluginCallGuard.RunOrAsync(
            pluginId.ToString(),
            nameof(GetStateAsync),
            () => GetStateCoreAsync(trackId, ct),
            (PluginLyricsState?)null,
            _logger
        );

    public Task<PluginWriteResult> WriteAsync(
        Guid trackId,
        PluginLyricsWrite write,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            pluginId.ToString(),
            nameof(WriteAsync),
            () => WriteCoreAsync(trackId, write, ct),
            PluginWriteResult.Refused,
            _logger
        );

    public Task<PluginWriteResult> ClearAsync(
        Guid trackId,
        long expectedVersion,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            pluginId.ToString(),
            nameof(ClearAsync),
            () => ClearCoreAsync(trackId, expectedVersion, ct),
            PluginWriteResult.Refused,
            _logger
        );

    private async Task<PluginLyricsState?> GetStateCoreAsync(Guid trackId, CancellationToken ct)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Track? track = await context
            .Tracks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == trackId, ct);
        if (track is null)
            return null;

        Lyric[]? lines = track.Lyrics;
        return new(
            lines?.Select(ToPluginLine).ToList(),
            track.LyricsSource,
            track.LyricsTiming,
            HasSyncedTiming(lines),
            track.LyricsOffset,
            track.LyricsVersion
        );
    }

    private async Task<PluginWriteResult> WriteCoreAsync(
        Guid trackId,
        PluginLyricsWrite write,
        CancellationToken ct
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Track? track = await context
            .Tracks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == trackId, ct);

        if (track is null)
            return Refuse(PluginLyricsRefusal.UnknownTrack, $"track {trackId} does not exist");

        if (track.LyricsVersion != write.ExpectedVersion)
            return VersionConflict(track.LyricsVersion, write.ExpectedVersion);

        // The host's rule, not the plugin's: timing a provider supplied is
        // what the owner already hears in sync, and a legacy row with times
        // but no recorded provenance is the same thing from before #72.
        if (HasSyncedProviderLyrics(track))
            return Refuse(
                PluginLyricsRefusal.ProviderSynced,
                "the track has synced provider lyrics, which a plugin never overwrites"
            );

        if (!KnownSources.Contains(write.Source))
            return Refuse(
                PluginLyricsRefusal.InvalidSource,
                $"'{write.Source}' is not lrclib, musixmatch, embedded or transcribed"
            );

        string? invalidLines = ValidateLines(write.Lines, track.Duration.ToSeconds());
        if (invalidLines is not null)
            return Refuse(PluginLyricsRefusal.InvalidLines, invalidLines);

        string json = JsonConvert.SerializeObject(write.Lines.Select(ToLyric).ToArray());

        // The offset was tuned for the previous timing. Kept, it would shift
        // lines that are now correct.
        int changed = await context
            .Tracks.Where(t => t.Id == trackId && t.LyricsVersion == write.ExpectedVersion)
            .ExecuteUpdateAsync(
                set =>
                    set.SetProperty(t => t._lyrics, json)
                        .SetProperty(t => t.LyricsSource, write.Source)
                        .SetProperty(t => t.LyricsTiming, LyricsTimings.Generated)
                        .SetProperty(t => t.LyricsOffset, (int?)null)
                        .SetProperty(t => t.LyricsVersion, t => t.LyricsVersion + 1),
                ct
            );

        // Checked once more at the write itself: another writer may have
        // landed between the read above and this statement.
        return changed == 0
            ? VersionConflict(track.LyricsVersion + 1, write.ExpectedVersion)
            : PluginWriteResult.Accepted();
    }

    private async Task<PluginWriteResult> ClearCoreAsync(
        Guid trackId,
        long expectedVersion,
        CancellationToken ct
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Track? track = await context
            .Tracks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == trackId, ct);

        if (track is null)
            return Refuse(PluginLyricsRefusal.UnknownTrack, $"track {trackId} does not exist");

        if (track.LyricsVersion != expectedVersion)
            return VersionConflict(track.LyricsVersion, expectedVersion);

        if (track.LyricsTiming != LyricsTimings.Generated)
            return Refuse(
                PluginLyricsRefusal.NotGenerated,
                $"the timing is '{track.LyricsTiming ?? "null"}', and only generated lyrics can be cleared"
            );

        // Back to "never checked": the resolver fetches provider lyrics again
        // on the next request, which is the state from before the plugin.
        int changed = await context
            .Tracks.Where(t => t.Id == trackId && t.LyricsVersion == expectedVersion)
            .ExecuteUpdateAsync(
                set =>
                    set.SetProperty(t => t._lyrics, (string?)null)
                        .SetProperty(t => t.LyricsSource, (string?)null)
                        .SetProperty(t => t.LyricsTiming, (string?)null)
                        .SetProperty(t => t.LyricsVersion, t => t.LyricsVersion + 1),
                ct
            );

        return changed == 0
            ? VersionConflict(track.LyricsVersion + 1, expectedVersion)
            : PluginWriteResult.Accepted();
    }

    /// <summary>
    /// Why the lines are not a timeline the player can follow, or null when
    /// they are. <paramref name="durationSeconds" /> is 0 when the library
    /// never recorded a duration; the upper bound is skipped then.
    /// </summary>
    internal static string? ValidateLines(IReadOnlyList<PluginLyricLine> lines, int durationSeconds)
    {
        if (lines.Count == 0)
            return "no lines";

        for (int i = 0; i < lines.Count; i++)
        {
            PluginLyricLine line = lines[i];

            if (line.Start < 0)
                return $"line {i} starts before the track ({line.Start} s)";

            if (durationSeconds > 0 && line.Start > durationSeconds)
                return $"line {i} starts after the track ends ({line.Start} s of {durationSeconds} s)";

            if (i > 0 && line.Start < lines[i - 1].Start)
                return $"line {i} starts before line {i - 1}; lines must be in order of start";

            string? invalidWords = ValidateWords(line, i);
            if (invalidWords is not null)
                return invalidWords;
        }

        return null;
    }

    private static string? ValidateWords(PluginLyricLine line, int index)
    {
        if (line.Words is not { Count: > 0 })
            return null;

        if (Math.Abs(line.Words[0].Start - line.Start) > TimeTolerance)
            return $"line {index} starts at {line.Start} s but its first word at {line.Words[0].Start} s";

        for (int j = 0; j < line.Words.Count; j++)
        {
            PluginLyricWord word = line.Words[j];

            if (word.End < word.Start)
                return $"word {j} of line {index} ends before it starts";

            if (j > 0 && word.Start < line.Words[j - 1].Start)
                return $"word {j} of line {index} starts before word {j - 1}; word times must be monotonic";
        }

        return null;
    }

    private static bool HasSyncedProviderLyrics(Track track) =>
        track.LyricsTiming == LyricsTimings.Provider
        || (track.LyricsTiming is null && HasSyncedTiming(track.Lyrics));

    private static bool HasSyncedTiming(Lyric[]? lines) =>
        lines?.Any(line => line.Time.Total > 0) ?? false;

    private static Lyric ToLyric(PluginLyricLine line) =>
        new()
        {
            Text = line.Text,
            Time = LyricTimeline.FromSeconds(line.Start),
            Words = line
                .Words?.Select(word => new LyricWord
                {
                    Text = word.Text,
                    Start = word.Start,
                    End = word.End,
                })
                .ToArray(),
        };

    private static PluginLyricLine ToPluginLine(Lyric line) =>
        new(
            line.Text,
            line.Time.Total,
            line.Words?.Select(word => new PluginLyricWord(word.Text, word.Start, word.End))
                .ToList()
        );

    private static PluginWriteResult VersionConflict(long actual, long expected) =>
        Refuse(
            PluginLyricsRefusal.VersionConflict,
            $"the lyrics are at version {actual}, the write expected version {expected}"
        );

    private static PluginWriteResult Refuse(string code, string reason) =>
        PluginWriteResult.Refused($"{code}: {reason}");
}
