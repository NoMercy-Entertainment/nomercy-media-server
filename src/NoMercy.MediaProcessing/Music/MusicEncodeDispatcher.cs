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

using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Music;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.AcoustId.Models;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;
using NoMercyQueue;

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// Turning an identified file into an encode, for both music import paths.
/// <para>
/// A library scan reaches the encoder through MusicLogic, and "Add new content"
/// reaches it through AudioImportJob. Only the scan path could encode, so a chosen
/// release stored its album and its tracks and then left every file sitting in the
/// download folder it came from. The layout rules and the dispatch live here so both
/// callers file the library the same way.
/// </para>
/// </summary>
public static class MusicEncodeDispatcher
{
    /// <summary>
    /// Which single track of the release this one file IS.
    /// <para>
    /// The fingerprint answers it outright when it identified a recording, because a
    /// MusicBrainz track carries the recording id it was pressed from. Only when the
    /// fingerprint found nothing does this fall back to the less certain reading of the
    /// file's own tags — the track number the file claims, then its title.
    /// </para>
    /// <para>
    /// Returning null is a real answer: the file was not identified, so it is stored but
    /// never encoded. Guessing here would write a file to disk under another track's name.
    /// </para>
    /// </summary>
    public static MusicBrainzTrack? ResolveTrackForFile(
        MusicBrainzReleaseAppends release,
        MediaFile file,
        AcoustIdFingerprintRecording? matchedRecording
    )
    {
        MusicBrainzTrack[] tracks = release.Media.SelectMany(media => media.Tracks).ToArray();

        if (matchedRecording is not null && matchedRecording.Id != Guid.Empty)
        {
            MusicBrainzTrack? byRecording = tracks.FirstOrDefault(track =>
                track.Recording.Id == matchedRecording.Id
            );

            if (byRecording is not null)
                return byRecording;
        }

        int taggedNumber = (int)(file.TagFile?.Tag?.Track ?? 0);
        if (taggedNumber > 0)
        {
            MusicBrainzTrack? byNumber = tracks.FirstOrDefault(track =>
                track.Position == taggedNumber
            );

            if (byNumber is not null)
                return byNumber;
        }

        string taggedTitle = (
            file.TagFile?.Tag?.Title ?? Path.GetFileNameWithoutExtension(file.Name) ?? string.Empty
        )
            .RemoveDiacritics()
            .RemoveNonAlphaNumericCharacters();

        if (string.IsNullOrWhiteSpace(taggedTitle))
            return null;

        return tracks.FirstOrDefault(track =>
            track
                .Title.RemoveDiacritics()
                .RemoveNonAlphaNumericCharacters()
                .Equals(taggedTitle, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// Hands the identified track to the encoder, which resolves the destination folder's
    /// presets and asks the model for its own sanitized filename fragment
    /// (<see cref="Track.CreateTitle"/>) rather than assembling a path here.
    /// </summary>
    public static async Task Dispatch(
        IStorageFactory storageFactory,
        Library library,
        Folder folder,
        MusicBrainzReleaseAppends release,
        MusicBrainzTrack track,
        MediaFile file,
        string inputFolder
    )
    {
        // Everything the encoder writes lands under BasePath. Pointing that at the source
        // would leave the encode sitting in the download folder it came from, so it is the
        // library destination, laid out by the Picard rules the library already follows.
        string albumFolder = Sanitize(release, track);

        QueueRunner.Current!.Dispatcher.Dispatch(
            new MusicEncodeJob
            {
                LibraryId = library.Id,
                FolderId = folder.Id,
                Id = release.Id,
                ReleaseId = release.Id,
                TrackId = track.Id,
                InputFolder = inputFolder,
                InputFile = file.Path,
                // Relative to the destination storage root, which is the library
                // folder — the same contract a video encode's OutputDirectory
                // follows. Sent as a full local path it was rejected outright:
                // the encoder refuses a rooted OutputDirectory, and 3,817 music
                // encodes failed on that one line without ever writing a byte.
                BasePath = albumFolder.Replace('\\', '/'),
                ArtistName =
                    release.ArtistCredit.FirstOrDefault()?.MusicBrainzArtist.Name ?? string.Empty,
                ReleaseName = release.Title,
                Year = release.ResolvedYear() ?? 0,
            }
        );
    }

    private static string Sanitize(MusicBrainzReleaseAppends release, MusicBrainzTrack track) =>
        PicardNaming.Sanitize(PicardNaming.BuildDirectory(NamingContextFor(release, track)));

    /// <summary>
    /// Reads the release the way the Picard script does: the album artist decides the
    /// folder, the track's own credit decides whether a performer is named on the file,
    /// and the disc and track totals decide the padding.
    /// </summary>
    public static MusicNamingContext NamingContextFor(
        MusicBrainzReleaseAppends release,
        MusicBrainzTrack track
    )
    {
        MusicBrainzMedia? medium = release.Media.FirstOrDefault(m =>
            m.Tracks.Any(t => t.Id == track.Id)
        );

        ReleaseArtistCredit? albumArtist = release.ArtistCredit.FirstOrDefault();
        ReleaseArtistCredit? trackArtist = track.ArtistCredit.FirstOrDefault();

        string credited = string.Join(
            ", ",
            track.ArtistCredit.Select(credit => credit.MusicBrainzArtist.Name)
        );
        string additional = string.Join(
            ", ",
            track.ArtistCredit.Skip(1).Select(credit => credit.MusicBrainzArtist.Name)
        );

        return new()
        {
            AlbumType = AlbumTypeFor(release),
            AlbumName = release.Title,
            Year = release.ResolvedYear(),
            AlbumArtistId = albumArtist?.MusicBrainzArtist.Id.ToString(),
            AlbumArtistSort =
                albumArtist?.MusicBrainzArtist.SortName ?? albumArtist?.MusicBrainzArtist.Name,
            AlbumArtistPrimary = albumArtist?.MusicBrainzArtist.Name,
            TrackTitle = track.Title,
            TrackArtistPrimary = trackArtist?.MusicBrainzArtist.Name,
            TrackArtistsCredited = credited,
            TrackArtistsAdditional = additional,
            TrackNumber = track.Position,
            TotalTracks = medium?.TrackCount ?? release.Media.Sum(m => m.TrackCount),
            DiscNumber = medium?.Position ?? 1,
            TotalDiscs = release.Media.Length,
        };
    }

    private static MusicAlbumType AlbumTypeFor(MusicBrainzReleaseAppends release)
    {
        string primary = release.MusicBrainzReleaseGroup?.PrimaryType ?? string.Empty;
        string[] secondary = release.MusicBrainzReleaseGroup?.SecondaryTypes ?? [];

        if (secondary.Any(type => type.Contains("soundtrack", StringComparison.OrdinalIgnoreCase)))
            return MusicAlbumType.Soundtrack;

        if (primary.Contains("other", StringComparison.OrdinalIgnoreCase))
            return MusicAlbumType.Other;

        bool single =
            release.Media.Length <= 1
            && release.Media.Sum(medium => medium.TrackCount) <= 1
            && primary.Contains("single", StringComparison.OrdinalIgnoreCase);

        return single ? MusicAlbumType.Single : MusicAlbumType.Standard;
    }
}
