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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// Resolves each item's newest video file through its own library folder's
/// storage (VideoFile.HostFolder is relative to that folder). An HLS encode is
/// read per rendition: one audio playlist and one video variant from the master.
/// Folders on a non-local driver are skipped: FFmpeg needs a local path, and
/// staging would copy every whole episode.
/// </summary>
public sealed class SegmentSourceResolver(
    IDbContextFactory<MediaContext> contextFactory,
    IStorageFactory storageFactory,
    ILogger<SegmentSourceResolver> logger
) : ISegmentSourceResolver
{
    private const string LocalDriverType = "local";

    private sealed record FileRow(
        Ulid Id,
        int? EpisodeId,
        int? MovieId,
        string Share,
        string HostFolder,
        string Filename
    );

    private sealed record Candidate(FileRow File, IStorage Storage, string Path, IReadOnlyList<string> Languages, HlsMasterPlaylist? Master, List<string> TrackLanguages);

    public async Task<List<ResolvedSegmentSource>> ResolveSeasonAsync(int seasonId, CancellationToken ct)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        string? originalLanguage = await context
            .Seasons.AsNoTracking()
            .Where(s => s.Id == seasonId)
            .Select(s => s.Tv.OriginalLanguage)
            .FirstOrDefaultAsync(ct);
        List<int> episodeIds = await context
            .Episodes.AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .OrderBy(e => e.EpisodeNumber)
            .Select(e => e.Id)
            .ToListAsync(ct);

        List<FileRow> rows = await context
            .VideoFiles.AsNoTracking()
            .Where(v => v.EpisodeId != null && episodeIds.Contains(v.EpisodeId.Value))
            .Select(v => new FileRow(v.Id, v.EpisodeId, v.MovieId, v.Share, v.HostFolder, v.Filename))
            .ToListAsync(ct);

        // Newest file per episode, in episode order (grouped in memory: no APPLY on SQLite).
        List<FileRow> newest =
        [
            .. episodeIds
                .Select(id => rows.Where(r => r.EpisodeId == id).OrderByDescending(r => r.Id).FirstOrDefault())
                .OfType<FileRow>(),
        ];

        List<Candidate> candidates = await CandidatesAsync(context, newest, ct);
        string? language = AudioLanguagePicker.Pick(originalLanguage, [.. candidates.Select(c => c.Languages)]);
        return [.. candidates.Select(c => Resolve(c, language)).OfType<ResolvedSegmentSource>()];
    }

    public async Task<ResolvedSegmentSource?> ResolveMovieAsync(int movieId, CancellationToken ct)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        string? originalLanguage = await context
            .Movies.AsNoTracking()
            .Where(m => m.Id == movieId)
            .Select(m => m.OriginalLanguage)
            .FirstOrDefaultAsync(ct);
        List<FileRow> rows = await context
            .VideoFiles.AsNoTracking()
            .Where(v => v.MovieId == movieId)
            .Select(v => new FileRow(v.Id, v.EpisodeId, v.MovieId, v.Share, v.HostFolder, v.Filename))
            .ToListAsync(ct);
        FileRow? newest = rows.OrderByDescending(r => r.Id).FirstOrDefault();
        if (newest is null)
            return null;

        List<Candidate> candidates = await CandidatesAsync(context, [newest], ct);
        string? language = AudioLanguagePicker.Pick(originalLanguage, [.. candidates.Select(c => c.Languages)]);
        return candidates.Select(c => Resolve(c, language)).FirstOrDefault(r => r is not null);
    }

    private async Task<List<Candidate>> CandidatesAsync(MediaContext context, List<FileRow> files, CancellationToken ct)
    {
        List<Ulid> folderIds = [.. files.Select(f => Ulid.TryParse(f.Share, out Ulid id) ? id : Ulid.Empty).Where(id => id != Ulid.Empty).Distinct()];
        Dictionary<Ulid, Folder> folders = await context
            .Folders.AsNoTracking()
            .Include(f => f.Driver)
            .Where(f => folderIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        List<Ulid> fileIds = [.. files.Select(f => f.Id)];
        List<(Ulid VideoFileId, Ulid Id, string Language)> tracks =
        [
            .. (
                await context
                    .VideoFileAudioTracks.AsNoTracking()
                    .Where(t => fileIds.Contains(t.VideoFileId))
                    .Select(t => new { t.VideoFileId, t.Id, t.Language })
                    .ToListAsync(ct)
            ).Select(t => (t.VideoFileId, t.Id, t.Language)),
        ];

        List<Candidate> candidates = [];
        foreach (FileRow file in files)
        {
            IStorage? storage = StorageFor(file, folders);
            if (storage is null)
                continue;

            string path = storage.CombinePath(file.HostFolder, file.Filename.TrimStart('/'));
            if (!storage.Exists(path))
            {
                logger.LogDebug("Segment analysis: {Path} not found, skipped", path);
                continue;
            }

            List<string> trackLanguages =
            [
                .. tracks.Where(t => t.VideoFileId == file.Id).OrderBy(t => t.Id).Select(t => t.Language),
            ];
            HlsMasterPlaylist? master = null;
            if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                master = HlsMasterPlaylist.Parse(await storage.ReadAllTextAsync(path, ct));

            List<string> languages = master is null
                ? trackLanguages
                : [.. master.Audio.Select(a => a.Language).OfType<string>()];
            candidates.Add(new(file, storage, path, languages, master, trackLanguages));
        }

        return candidates;
    }

    private static ResolvedSegmentSource? Resolve(Candidate candidate, string? language)
    {
        FileRow file = candidate.File;
        string folder = candidate.Storage.GetParent(candidate.Path) ?? string.Empty;

        if (candidate.Master is { } master)
        {
            if (master.VideoUris.Count == 0 || master.Audio.Count == 0)
                return null;

            HlsAudioRendition audio =
                master.Audio.FirstOrDefault(a => AudioLanguagePicker.Same(a.Language, language))
                ?? master.Audio.FirstOrDefault(a => a.IsDefault)
                ?? master.Audio[0];
            return new(
                file.EpisodeId,
                file.MovieId,
                new(
                    candidate.Storage,
                    candidate.Storage.CombinePath(folder, audio.Uri),
                    0,
                    candidate.Storage.CombinePath(folder, master.VideoUris[0])
                )
            );
        }

        int index = candidate.TrackLanguages.FindIndex(l => AudioLanguagePicker.Same(l, language));
        return new(
            file.EpisodeId,
            file.MovieId,
            new(candidate.Storage, candidate.Path, Math.Max(0, index), candidate.Path)
        );
    }

    private IStorage? StorageFor(FileRow file, Dictionary<Ulid, Folder> folders)
    {
        if (!Ulid.TryParse(file.Share, out Ulid folderId) || !folders.TryGetValue(folderId, out Folder? folder))
            return null;

        if (
            folder.Driver is not null
            && !string.Equals(folder.Driver.Type, LocalDriverType, StringComparison.OrdinalIgnoreCase)
        )
        {
            logger.LogInformation(
                "Segment analysis: folder {Folder} uses the {Driver} driver; only local folders are analysed",
                folder.Path,
                folder.Driver.Type
            );
            return null;
        }

        return storageFactory.For(folder.Id, folder.DriverId, string.Empty);
    }
}
