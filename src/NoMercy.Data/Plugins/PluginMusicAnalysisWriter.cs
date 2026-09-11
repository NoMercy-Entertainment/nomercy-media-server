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
    ) =>
        PluginCallGuard.RunAsync(
            Operation(nameof(UpsertDjAnalysisAsync)),
            () => UpsertDjAnalysisCoreAsync(record, ct),
            PluginWriteResult.Refused,
            _logger
        );

    public Task<PluginWriteResult> RegisterStemAsync(
        PluginTrackStem stem,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            Operation(nameof(RegisterStemAsync)),
            () => RegisterStemsCoreAsync([stem], ct),
            PluginWriteResult.Refused,
            _logger
        );

    public Task<PluginWriteResult> RegisterStemsAsync(
        IReadOnlyList<PluginTrackStem> stems,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            Operation(nameof(RegisterStemsAsync)),
            () => RegisterStemsCoreAsync(stems, ct),
            PluginWriteResult.Refused,
            _logger
        );

    public Task<PluginWriteResult> MarkFailedAsync(
        Guid trackId,
        int djAnalyzerVersion,
        int baseAnalyzerVersion,
        string reason,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            Operation(nameof(MarkFailedAsync)),
            () => MarkFailedCoreAsync(trackId, djAnalyzerVersion, baseAnalyzerVersion, reason, ct),
            PluginWriteResult.Refused,
            _logger
        );

    /// <summary>
    /// The one member with no refusal channel, so a failure has nowhere to go
    /// but the log: deleting a row that is not there is already a no-op, and a
    /// caller that asked for a row to be gone is no worse off being told
    /// nothing than being handed a driver exception.
    /// </summary>
    public Task DeleteDjAnalysisAsync(Guid trackId, CancellationToken ct = default) =>
        PluginCallGuard.RunAsync(
            Operation(nameof(DeleteDjAnalysisAsync)),
            () => DeleteDjAnalysisCoreAsync(trackId, ct),
            _logger
        );

    /// <summary>Which plugin's call this is, for the guard's warning line.</summary>
    private string Operation(string member) => $"plugin {pluginId}: {member}";

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

        if (BeforeSave is not null)
        {
            await BeforeSave();
        }

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
        {
            return await ResolveFailedDjWriteAsync(record.TrackId, exception, ct);
        }

        return PluginWriteResult.Accepted();
    }

    /// <summary>
    /// A save that did not land is only a race when a row is there now: two
    /// sweeps upserting one track both read "no row" and both insert, and the
    /// loser can simply run again. With no row, the save failed for a reason
    /// of its own - a foreign key, a column constraint, a disk - and "retry"
    /// would hide that for ever, so it is logged and named instead.
    /// </summary>
    private async Task<PluginWriteResult> ResolveFailedDjWriteAsync(
        Guid trackId,
        DbUpdateException exception,
        CancellationToken ct
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        bool stored = await context
            .TrackDjAnalysis.AsNoTracking()
            .AnyAsync(analysis => analysis.TrackId == trackId, ct);

        if (stored)
            return PluginWriteResult.Refused(
                $"the DJ record for track {trackId} was written concurrently; retry"
            );

        _logger.LogWarning(
            exception,
            "plugin {PluginId}: the DJ record for track {TrackId} could not be stored",
            pluginId,
            trackId
        );

        return PluginWriteResult.Refused(
            $"the DJ record for track {trackId} could not be stored: {exception.GetType().Name}"
        );
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
        if (stems is null)
            return PluginWriteResult.Refused("stems must not be null");

        if (stems.Count == 0)
            return PluginWriteResult.Accepted();

        PluginWriteResult? duplicate = CheckForDuplicates(stems);
        if (duplicate is not null)
            return duplicate;

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
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(ct);
            return await ResolveFailedStemWriteAsync(stems, exception, ct);
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
        // it does not hold, but is checked first: asking the store means
        // slicing the key into a path.
        if (
            !DerivedAudioKey.IsValid(stem.StorageKey)
            || !await store.ExistsAsync(stem.StorageKey, ct)
        )
            return PluginWriteResult.Refused(
                $"storage key {stem.StorageKey} is not in the derived store"
            );

        string? contentType = await context
            .DerivedAudio.AsNoTracking()
            .Where(row => row.Key == stem.StorageKey)
            .Select(row => row.ContentType)
            .FirstOrDefaultAsync(ct);

        if (contentType is null)
            return PluginWriteResult.Refused(
                $"storage key {stem.StorageKey} is not in the derived store"
            );

        // The register row is what a client is handed the stem as, so a row
        // claiming Opus over a FLAC file is a player error hours later.
        if (!FormatMatchesContentType(stem.Format, contentType))
            return PluginWriteResult.Refused(
                $"stem format {stem.Format} does not match the stored content type {contentType}"
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

    /// <summary>
    /// One register row is addressed by (track, kind, coverage, producer), so
    /// two entries sharing all four are one row written twice: whichever came
    /// second would silently win, which is never what a caller meant.
    /// </summary>
    private static PluginWriteResult? CheckForDuplicates(IReadOnlyList<PluginTrackStem> stems)
    {
        HashSet<(Guid, string, PluginStemCoverage, string)> seen = [];

        foreach (PluginTrackStem stem in stems)
        {
            if (!seen.Add((stem.TrackId, stem.Kind, stem.Coverage, stem.ProducerVersion)))
                return PluginWriteResult.Refused(
                    $"stems contains the same stem twice: {stem.Kind}/{stem.Coverage}"
                );
        }

        return null;
    }

    /// <summary>
    /// The pairings the derived store can hold today. Anything else is a
    /// mismatch rather than an unknown: a format that cannot come out of that
    /// container is not a row worth keeping.
    /// </summary>
    private static bool FormatMatchesContentType(string format, string contentType) =>
        (format.ToLowerInvariant(), contentType.ToLowerInvariant()) switch
        {
            ("opus", "audio/ogg") => true,
            ("opus", "audio/opus") => true,
            ("flac", "audio/flac") => true,
            _ => false,
        };

    /// <summary>Adds or updates one stem's row on the context, without saving it.</summary>
    private static async Task StageStemAsync(
        MediaContext context,
        PluginTrackStem stem,
        CancellationToken ct
    )
    {
        StemCoverage coverage = StemCoverageMap.ToDb(stem.Coverage);

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
    /// A save that did not land is only a race when the row is there now: two
    /// sweeps registering one stem both read "no row" and both insert, and the
    /// winner stored either the same key (nothing is lost, so the loser is
    /// told it landed) or another one (the caller has to run again). With no
    /// row at all the save failed for a reason of its own - a foreign key, a
    /// column constraint, a disk - which is logged and named rather than
    /// dressed up as something a retry would fix.
    /// </summary>
    private async Task<PluginWriteResult> ResolveFailedStemWriteAsync(
        IReadOnlyList<PluginTrackStem> stems,
        DbUpdateException exception,
        CancellationToken ct
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        foreach (PluginTrackStem stem in stems)
        {
            StemCoverage coverage = StemCoverageMap.ToDb(stem.Coverage);

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

            if (stored is null)
            {
                _logger.LogWarning(
                    exception,
                    "plugin {PluginId}: stem {Kind}/{Coverage} for track {TrackId} could not be stored",
                    pluginId,
                    stem.Kind,
                    stem.Coverage,
                    stem.TrackId
                );

                return PluginWriteResult.Refused(
                    $"stem {stem.Kind}/{stem.Coverage} for track {stem.TrackId} could not be stored: {exception.GetType().Name}"
                );
            }

            if (stored.StorageKey != stem.StorageKey)
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
}
