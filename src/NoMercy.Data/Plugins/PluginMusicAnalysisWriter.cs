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
using Microsoft.EntityFrameworkCore.Storage;
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
/// <para>
/// That holds for failures nobody planned for too - a locked database file, a
/// disk that went away mid-write. Every member here catches what it did not
/// expect, logs it at Warning and refuses with the exception's type name, so
/// one bad track costs a plugin one refusal rather than its whole sweep.
/// Cancellation the caller asked for is the one thing that still propagates.
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

    public Task<PluginWriteResult> UpsertDjAnalysisAsync(
        PluginTrackDjAnalysis record,
        CancellationToken ct = default
    ) => GuardAsync(nameof(UpsertDjAnalysisAsync), () => UpsertDjAnalysisCoreAsync(record, ct));

    public Task<PluginWriteResult> RegisterStemAsync(
        PluginTrackStem stem,
        CancellationToken ct = default
    ) => GuardAsync(nameof(RegisterStemAsync), () => RegisterStemsCoreAsync([stem], ct));

    public Task<PluginWriteResult> RegisterStemsAsync(
        IReadOnlyList<PluginTrackStem> stems,
        CancellationToken ct = default
    ) => GuardAsync(nameof(RegisterStemsAsync), () => RegisterStemsCoreAsync(stems, ct));

    public Task<PluginWriteResult> MarkFailedAsync(
        Guid trackId,
        int djAnalyzerVersion,
        int baseAnalyzerVersion,
        string reason,
        CancellationToken ct = default
    ) =>
        GuardAsync(
            nameof(MarkFailedAsync),
            () => MarkFailedCoreAsync(trackId, djAnalyzerVersion, baseAnalyzerVersion, reason, ct)
        );

    /// <summary>
    /// The one member with no refusal channel, so a failure has nowhere to go
    /// but the log: deleting a row that is not there is already a no-op, and a
    /// caller that asked for a row to be gone is no worse off being told
    /// nothing than being handed a driver exception.
    /// </summary>
    public async Task DeleteDjAnalysisAsync(Guid trackId, CancellationToken ct = default)
    {
        try
        {
            await DeleteDjAnalysisCoreAsync(trackId, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(exception, nameof(DeleteDjAnalysisAsync));
        }
    }

    private async Task<PluginWriteResult> UpsertDjAnalysisCoreAsync(
        PluginTrackDjAnalysis record,
        CancellationToken ct
    )
    {
        PluginWriteResult? missingList = CheckListsPresent(record);
        if (missingList is not null)
            return missingList;

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

        PluginWriteResult? malformedRegion = CheckVocalRegionShape(record.VocalRegionsMs);
        if (malformedRegion is not null)
            return malformedRegion;

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

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two sweeps upserting the same track at once: both read "no row"
            // and both tried to insert. Nothing is lost - the winner wrote the
            // same kind of record - so the loser is told to run again rather
            // than handed a driver exception.
            return PluginWriteResult.Refused(
                $"the DJ record for track {record.TrackId} was written concurrently; retry"
            );
        }

        return PluginWriteResult.Accepted();
    }

    /// <summary>
    /// Runs between staging the rows and saving them, so a test can land a
    /// competing write in exactly the window this method has to survive. Never
    /// set in production.
    /// </summary>
    internal Func<Task>? BeforeSave { get; init; }

    /// <summary>
    /// Every stem is validated before any of them is staged, and all of them
    /// are saved in one transaction, so a refusal on the second stem of a pair
    /// leaves the first one unwritten rather than half a split in the register.
    /// </summary>
    private async Task<PluginWriteResult> RegisterStemsCoreAsync(
        IReadOnlyList<PluginTrackStem> stems,
        CancellationToken ct
    )
    {
        if (stems.Count == 0)
            return PluginWriteResult.Accepted();

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        foreach (PluginTrackStem stem in stems)
        {
            PluginWriteResult? refusal = await CheckStemAsync(context, stem, ct);
            if (refusal is not null)
                return refusal;
        }

        foreach (PluginTrackStem stem in stems)
        {
            await StageStemAsync(context, stem, ct);
        }

        if (BeforeSave is not null)
        {
            await BeforeSave();
        }

        await using IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(ct);

        try
        {
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            return await ResolveConcurrentStemWriteAsync(stems, ct);
        }

        return PluginWriteResult.Accepted();
    }

    /// <summary>Why one stem cannot be written, or null when it can.</summary>
    private async Task<PluginWriteResult?> CheckStemAsync(
        MediaContext context,
        PluginTrackStem stem,
        CancellationToken ct
    )
    {
        bool trackExists = await context
            .Tracks.AsNoTracking()
            .AnyAsync(t => t.Id == stem.TrackId, ct);

        if (!trackExists)
            return PluginWriteResult.Refused($"track {stem.TrackId} does not exist");

        // A key the store could never have minted gets the same answer as one
        // it simply does not hold, but it is checked here first: asking the
        // store means slicing the key into a path.
        if (
            !DerivedAudioKey.IsValid(stem.StorageKey)
            || !await store.ExistsAsync(stem.StorageKey, ct)
        )
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

        return null;
    }

    /// <summary>Adds or updates one stem's row on the context, without saving it.</summary>
    private static async Task StageStemAsync(
        MediaContext context,
        PluginTrackStem stem,
        CancellationToken ct
    )
    {
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
    }

    /// <summary>
    /// Two sweeps registering the same stem at once both read "no row" and
    /// both insert; the loser's save hits the unique index. When the winner
    /// stored the same key, nothing is lost and the loser is told it landed;
    /// when it stored another one, the caller has to run again.
    /// </summary>
    private async Task<PluginWriteResult> ResolveConcurrentStemWriteAsync(
        IReadOnlyList<PluginTrackStem> stems,
        CancellationToken ct
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        foreach (PluginTrackStem stem in stems)
        {
            StemCoverage coverage = ToDbCoverage(stem.Coverage);

            TrackStem? stored = await context
                .TrackStems.AsNoTracking()
                .FirstOrDefaultAsync(
                    row =>
                        row.TrackId == stem.TrackId
                        && row.Kind == stem.Kind
                        && row.Coverage == coverage
                        && row.ProducerVersion == stem.ProducerVersion,
                    ct
                );

            if (stored is null || stored.StorageKey != stem.StorageKey)
                return PluginWriteResult.Refused(
                    $"stem {stem.Kind}/{stem.Coverage} for track {stem.TrackId} was written concurrently; retry"
                );
        }

        return PluginWriteResult.Accepted();
    }

    private async Task<PluginWriteResult> MarkFailedCoreAsync(
        Guid trackId,
        int djAnalyzerVersion,
        int baseAnalyzerVersion,
        string reason,
        CancellationToken ct
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

    private async Task DeleteDjAnalysisCoreAsync(Guid trackId, CancellationToken ct)
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
    /// A vocal region is exactly [start, end] with start before end. One value
    /// reads as a region ending wherever the next one starts, and a backwards
    /// pair as a region of negative length - both are a planner reading the
    /// wrong seconds of a track long after the sweep that stored them.
    /// </summary>
    private static PluginWriteResult? CheckVocalRegionShape(IReadOnlyList<int[]> regions)
    {
        for (int index = 0; index < regions.Count; index++)
        {
            int[] region = regions[index];
            if (region.Length != 2 || region[0] >= region[1])
                return PluginWriteResult.Refused(
                    $"vocal_regions_ms entry {index} must be [start, end] with start < end"
                );
        }

        return null;
    }

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

    /// <summary>
    /// Runs one contract call and turns anything it throws into a refusal:
    /// every caller here is a plugin sweeping unattended, and an exception
    /// crossing the host boundary takes that whole sweep down instead of one
    /// track. The exception type is named in the refusal so the owner can match
    /// it against the Warning line this also writes; cancellation the caller
    /// asked for is passed through untouched.
    /// </summary>
    private async Task<PluginWriteResult> GuardAsync(
        string member,
        Func<Task<PluginWriteResult>> call
    )
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(exception, member);
            return PluginWriteResult.Refused(
                $"the server could not complete this call: {exception.GetType().Name}"
            );
        }
    }

    private void LogUnexpected(Exception exception, string member) =>
        _logger.LogWarning(
            exception,
            "plugin {PluginId}: {Member} failed inside the server",
            pluginId,
            member
        );

    /// <summary>
    /// Every list on the record is required. A null one is a caller bug that
    /// would otherwise serialise to the JSON literal <c>null</c> and land in a
    /// column the reader reads as "the plugin stored nothing here" - which is
    /// a different claim from the <c>[]</c> an empty measurement writes.
    /// </summary>
    private static PluginWriteResult? CheckListsPresent(PluginTrackDjAnalysis record)
    {
        if (record.PhraseStartsMs is null)
            return PluginWriteResult.Refused("phrase_starts_ms must not be null");

        if (record.VocalRegionsMs is null)
            return PluginWriteResult.Refused("vocal_regions_ms must not be null");

        if (record.BarEnergy is null)
            return PluginWriteResult.Refused("bar_energy must not be null");

        if (record.CuePoints is null)
            return PluginWriteResult.Refused("cue_points must not be null");

        if (record.Chords is null)
            return PluginWriteResult.Refused("chords must not be null");

        return null;
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
