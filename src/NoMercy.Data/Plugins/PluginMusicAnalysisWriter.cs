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

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Writes the DJ analysis record and the stem register on behalf of one
/// plugin - <see cref="PluginMusicQuery" />'s write side.
/// <para>
/// Every write is validated before it touches the database, and every
/// rejection comes back as a <see cref="PluginWriteResult" /> refusal in
/// words rather than an exception: a plugin's sweep runs unattended, so a
/// stack trace nobody reads is worth nothing next to a reason the owner can
/// act on.
/// </para>
/// </summary>
public class PluginMusicAnalysisWriter(
    Ulid pluginId,
    IDbContextFactory<MediaContext> contextFactory,
    IDerivedAudioStore store,
    ILogger<PluginMusicAnalysisWriter>? logger = null
) : IPluginMusicAnalysisWriter
{
    /// <summary>Mirrors the <c>[MaxLength(65536)]</c> on every JSON text column.</summary>
    private const int MaxJsonColumnLength = 65536;

    /// <summary>Mirrors the <c>[MaxLength(1024)]</c> on <c>FailureReason</c>.</summary>
    private const int MaxFailureReasonLength = 1024;

    private readonly ILogger<PluginMusicAnalysisWriter> _logger =
        logger ?? NullLogger<PluginMusicAnalysisWriter>.Instance;

    public async Task<PluginWriteResult> UpsertDjAnalysisAsync(
        PluginTrackDjAnalysis record,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Track? track = await context
            .Tracks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == record.TrackId, ct);

        if (track is null)
            return PluginWriteResult.Refused($"track {record.TrackId} does not exist");

        bool hasCurrentBaseRow = await context
            .TrackAudioAnalysis.AsNoTracking()
            .AnyAsync(
                analysis =>
                    analysis.TrackId == record.TrackId
                    && analysis.State == AudioAnalysisState.Ok
                    && analysis.AnalyzerVersion == record.BaseAnalyzerVersion,
                ct
            );

        if (!hasCurrentBaseRow)
            return PluginWriteResult.Refused(
                $"no base analysis at version {record.BaseAnalyzerVersion} for track {record.TrackId}"
            );

        if (record.BeatsPerBar < 1)
            return PluginWriteResult.Refused(
                $"beats_per_bar must be at least 1, got {record.BeatsPerBar}"
            );

        if (
            record.DownbeatIndex is int downbeatIndex
            && (downbeatIndex < 0 || downbeatIndex > record.BeatsPerBar - 1)
        )
            return PluginWriteResult.Refused(
                $"downbeat_index value {downbeatIndex} lies outside the beat grid (0..{record.BeatsPerBar - 1})"
            );

        double? durationSeconds = PluginMusicQuery.ParseDurationSeconds(track.Duration);

        if (durationSeconds is null)
        {
            _logger.LogInformation(
                "Track {TrackId}: duration is unknown; skipping the millisecond-range check on its DJ analysis",
                record.TrackId
            );
        }
        else
        {
            PluginWriteResult? outOfRange = CheckMsRange(record, durationSeconds.Value * 1000);
            if (outOfRange is not null)
                return outOfRange;
        }

        if (!IsAscending(record.PhraseStartsMs))
            return PluginWriteResult.Refused("phrase_starts_ms must be ascending");

        string phraseStartsMsJson = JsonSerializer.Serialize(record.PhraseStartsMs);
        string vocalRegionsMsJson = JsonSerializer.Serialize(record.VocalRegionsMs);
        string barEnergyJson = JsonSerializer.Serialize(record.BarEnergy);
        string cuePointsJson = JsonSerializer.Serialize(
            record.CuePoints.Select(cue => new
            {
                ms = cue.Ms,
                type = cue.Type,
                direction = cue.Direction,
                score = cue.Score,
            })
        );
        string chordsJson = JsonSerializer.Serialize(
            record.Chords.Select(chord => new { ms = chord.Ms, chord = chord.Chord })
        );

        PluginWriteResult? tooLarge = CheckJsonSize(
            ("phrase_starts_ms", phraseStartsMsJson),
            ("vocal_regions_ms", vocalRegionsMsJson),
            ("bar_energy", barEnergyJson),
            ("cue_points", cuePointsJson),
            ("chords", chordsJson)
        );
        if (tooLarge is not null)
            return tooLarge;

        TrackDjAnalysis? existing = await context.TrackDjAnalysis.FirstOrDefaultAsync(
            analysis => analysis.TrackId == record.TrackId,
            ct
        );

        if (existing is null)
        {
            existing = new TrackDjAnalysis { TrackId = record.TrackId };
            context.TrackDjAnalysis.Add(existing);
        }

        existing.ProducerPluginId = pluginId;
        existing.DjAnalyzerVersion = record.DjAnalyzerVersion;
        existing.BaseAnalyzerVersion = record.BaseAnalyzerVersion;
        existing.State = AudioAnalysisState.Ok;
        existing.FailureReason = null;
        existing.DownbeatIndex = record.DownbeatIndex;
        existing.BeatsPerBar = record.BeatsPerBar;
        existing.PhraseLengthBars = record.PhraseLengthBars;
        existing.PhraseStartsMs = phraseStartsMsJson;
        existing.VocalRegionsMs = vocalRegionsMsJson;
        existing.BarEnergy = barEnergyJson;
        existing.CuePoints = cuePointsJson;
        existing.Chords = chordsJson;
        existing.AnalyzedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
        return PluginWriteResult.Accepted();
    }

    public async Task<PluginWriteResult> RegisterStemAsync(
        PluginTrackStem stem,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        bool trackExists = await context
            .Tracks.AsNoTracking()
            .AnyAsync(t => t.Id == stem.TrackId, ct);

        if (!trackExists)
            return PluginWriteResult.Refused($"track {stem.TrackId} does not exist");

        if (!await store.ExistsAsync(stem.StorageKey, ct))
            return PluginWriteResult.Refused(
                $"storage key {stem.StorageKey} is not in the derived store"
            );

        if (stem.Coverage == PluginStemCoverage.Full)
        {
            if (stem.WindowStartMs is not null || stem.WindowEndMs is not null)
                return PluginWriteResult.Refused("full coverage stems must not specify a window");
        }
        else
        {
            if (stem.WindowStartMs is null || stem.WindowEndMs is null)
                return PluginWriteResult.Refused(
                    "windowed stems must specify both window_start_ms and window_end_ms"
                );

            if (stem.WindowStartMs.Value >= stem.WindowEndMs.Value)
                return PluginWriteResult.Refused("window_start_ms must be less than window_end_ms");
        }

        StemCoverage coverage = ToDbCoverage(stem.Coverage);

        TrackStem? existing = await context.TrackStems.FirstOrDefaultAsync(
            row =>
                row.TrackId == stem.TrackId
                && row.Kind == stem.Kind
                && row.Coverage == coverage
                && row.ProducerVersion == stem.ProducerVersion,
            ct
        );

        if (existing is null)
        {
            existing = new TrackStem { Id = Ulid.NewUlid(), TrackId = stem.TrackId };
            context.TrackStems.Add(existing);
        }

        existing.Kind = stem.Kind;
        existing.Coverage = coverage;
        existing.WindowStartMs = stem.WindowStartMs;
        existing.WindowEndMs = stem.WindowEndMs;
        existing.Format = stem.Format;
        existing.SampleRate = stem.SampleRate;
        existing.StorageKey = stem.StorageKey;
        existing.ProducerVersion = stem.ProducerVersion;
        existing.CreatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
        return PluginWriteResult.Accepted();
    }

    public async Task<PluginWriteResult> MarkFailedAsync(
        Guid trackId,
        int djAnalyzerVersion,
        int baseAnalyzerVersion,
        string reason,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        bool trackExists = await context.Tracks.AsNoTracking().AnyAsync(t => t.Id == trackId, ct);

        if (!trackExists)
            return PluginWriteResult.Refused($"track {trackId} does not exist");

        string truncatedReason =
            reason.Length > MaxFailureReasonLength ? reason[..MaxFailureReasonLength] : reason;

        TrackDjAnalysis? existing = await context.TrackDjAnalysis.FirstOrDefaultAsync(
            analysis => analysis.TrackId == trackId,
            ct
        );

        if (existing is null)
        {
            existing = new TrackDjAnalysis { TrackId = trackId };
            context.TrackDjAnalysis.Add(existing);
        }

        existing.ProducerPluginId = pluginId;
        existing.DjAnalyzerVersion = djAnalyzerVersion;
        existing.BaseAnalyzerVersion = baseAnalyzerVersion;
        existing.State = AudioAnalysisState.Failed;
        existing.FailureReason = truncatedReason;
        existing.AnalyzedAt = DateTime.UtcNow;

        // A Failed row must not go on carrying a previous Ok row's
        // measurements: a caller reading DjAnalyzerVersion = 4 has to be able
        // to trust that either the row is Ok and current, or it has nothing.
        // Null is the right value here - the one place it is, since the
        // reader only ever surfaces Ok rows.
        existing.DownbeatIndex = null;
        existing.BeatsPerBar = 4;
        existing.PhraseLengthBars = 8;
        existing.PhraseStartsMs = null;
        existing.VocalRegionsMs = null;
        existing.BarEnergy = null;
        existing.CuePoints = null;
        existing.Chords = null;

        await context.SaveChangesAsync(ct);
        return PluginWriteResult.Accepted();
    }

    public async Task DeleteDjAnalysisAsync(Guid trackId, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        TrackDjAnalysis? existing = await context.TrackDjAnalysis.FirstOrDefaultAsync(
            analysis => analysis.TrackId == trackId,
            ct
        );

        if (existing is null)
            return;

        context.TrackDjAnalysis.Remove(existing);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Every millisecond-denominated value in <paramref name="record" /> -
    /// phrase starts, vocal region bounds, cue points, chord timestamps - has
    /// to fall inside the track, checked in that order so the first offender
    /// is always the one reported.
    /// </summary>
    private static PluginWriteResult? CheckMsRange(PluginTrackDjAnalysis record, double durationMs)
    {
        foreach (int value in record.PhraseStartsMs)
        {
            if (IsOutOfRange(value, durationMs))
                return OutOfRangeRefusal("phrase_starts_ms", value, durationMs);
        }

        foreach (int[] region in record.VocalRegionsMs)
        {
            foreach (int value in region)
            {
                if (IsOutOfRange(value, durationMs))
                    return OutOfRangeRefusal("vocal_regions_ms", value, durationMs);
            }
        }

        foreach (PluginCuePoint cue in record.CuePoints)
        {
            if (IsOutOfRange(cue.Ms, durationMs))
                return OutOfRangeRefusal("cue_points", cue.Ms, durationMs);
        }

        foreach (PluginChord chord in record.Chords)
        {
            if (IsOutOfRange(chord.Ms, durationMs))
                return OutOfRangeRefusal("chords", chord.Ms, durationMs);
        }

        return null;
    }

    private static bool IsOutOfRange(int value, double durationMs) =>
        value < 0 || value > durationMs;

    private static PluginWriteResult OutOfRangeRefusal(
        string field,
        int value,
        double durationMs
    ) =>
        PluginWriteResult.Refused(
            $"{field} value {value} lies outside the track (0..{(long)Math.Round(durationMs)} ms)"
        );

    /// <summary>
    /// Phrase boundaries strictly increase: two phrases cannot start at the
    /// same millisecond, so equal neighbours fail this the same as a drop.
    /// </summary>
    private static bool IsAscending(IReadOnlyList<int> values)
    {
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] <= values[i - 1])
                return false;
        }

        return true;
    }

    private static PluginWriteResult? CheckJsonSize(params (string Field, string Json)[] columns)
    {
        foreach ((string field, string json) in columns)
        {
            if (json.Length > MaxJsonColumnLength)
                return PluginWriteResult.Refused($"{field} exceeds 64 kB");
        }

        return null;
    }

    /// <summary>
    /// Maps the plugin's stem-coverage enum to the database's own. An
    /// explicit switch, the mirror of <see cref="PluginMusicQuery" />'s own
    /// mapping the other way, rather than a cast the two enums only happen to
    /// agree on today.
    /// </summary>
    private static StemCoverage ToDbCoverage(PluginStemCoverage coverage) =>
        coverage switch
        {
            PluginStemCoverage.Full => StemCoverage.Full,
            PluginStemCoverage.MixIn => StemCoverage.MixIn,
            PluginStemCoverage.MixOut => StemCoverage.MixOut,
            _ => throw new ArgumentOutOfRangeException(
                nameof(coverage),
                coverage,
                "Unknown stem coverage"
            ),
        };
}
