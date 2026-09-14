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

using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;

namespace NoMercy.Api.DTOs.Media;

public class VideoPlaylistResponseDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("show")]
    public string? Show { get; set; }

    [JsonProperty("origin")]
    public Guid Origin { get; set; }

    [JsonProperty("uuid")]
    public int Uuid { get; set; }

    [JsonProperty("video_id")]
    public Ulid VideoId { get; set; }

    [JsonProperty("duration")]
    public string Duration { get; set; } = string.Empty;

    [JsonProperty("tmdb_id")]
    public int TmdbId { get; set; }

    [JsonProperty("video_type")]
    public string VideoType { get; set; } = string.Empty;

    [JsonProperty("library_type")]
    public string LibraryType { get; set; } = string.Empty;

    [JsonProperty("playlist_type")]
    public string PlaylistType { get; set; } = string.Empty;

    [JsonProperty("playlist_id")]
    public dynamic PlaylistId { get; set; } = null!;

    [JsonProperty("year")]
    public long Year { get; set; }

    [JsonProperty("file")]
    public string File { get; set; } = string.Empty;

    [JsonProperty("progress")]
    public ProgressDto? Progress { get; set; }

    [JsonProperty("image")]
    public string? Image { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("sources")]
    public SourceDto[] Sources { get; set; } = [];

    [JsonProperty("fonts")]
    public List<IFont> Fonts { get; set; } = [];

    [JsonProperty("chapters")]
    public List<IChapter> Chapters { get; set; } = [];

    [JsonProperty("tracks")]
    public List<VideoTrack> Tracks { get; set; } = [];

    [JsonProperty("rating")]
    public RatingClass? ContentRating { get; set; }

    [JsonProperty("audio")]
    public List<IAudio> Audio { get; set; } = [];

    [JsonProperty("captions")]
    public List<ISubtitle> Captions { get; set; } = [];

    [JsonProperty("qualities")]
    public List<IVideo> Qualities { get; set; } = [];

    [JsonProperty("season")]
    public int? Season { get; set; }

    [JsonProperty("episode")]
    public int? Episode { get; set; }

    [JsonProperty("seasonName")]
    public string? SeasonName { get; set; }

    [JsonProperty("episode_id")]
    public int? EpisodeId { get; set; }

    public VideoPlaylistResponseDto() { }

    public VideoPlaylistResponseDto(
        Episode episode,
        string playlistType,
        dynamic playlistId,
        string country,
        int? index = null
    )
    {
        VideoFile? videoFile = episode.VideoFiles.FirstOrDefault();
        if (videoFile is null)
            return;

        if (episode.Tv is null)
            return;

        string baseFolder = $"/{videoFile.Share}{videoFile.Folder}".EncodePath();

        string? logo = episode
            .Tv.Images.OrderByDescending(image => image.VoteAverage)
            .FirstOrDefault(image => image.Type == "logo")
            ?.FilePath;

        string tvTitle = episode.Tv.Translations.FirstOrDefault()?.Title ?? episode.Tv.Title;

        string? title = episode.Translations.FirstOrDefault()?.Title ?? episode.Title;
        string? overview = episode.Translations.FirstOrDefault()?.Overview ?? episode.Overview;

        string? specialTitle = index is not null
            ? $"{tvTitle} %S{episode.SeasonNumber} %E{episode.EpisodeNumber} - {title}"
            : title;

        Subs subs = Subtitles(videoFile);
        Id = episode.Id;
        Title = specialTitle;
        Description = overview;
        Show = index is not null ? null : tvTitle;
        Origin = Info.DeviceId;
        Uuid = episode.Tv.Id + episode.Id;
        Duration = videoFile.Duration ?? "0";
        TmdbId = episode.Tv.Id;
        VideoType = MediaTypes.TvMediaType;
        VideoId = videoFile.Id;
        LibraryType = episode.Tv.MediaType ?? MediaTypes.TvMediaType;
        PlaylistType = playlistType;
        PlaylistId = playlistId;
        Year = episode.Tv.FirstAirDate.ParseYear();
        Image = episode.Still;
        Logo = logo;
        ApplyVideoFile(videoFile, baseFolder, subs);
        Season = index is not null ? 0 : episode.SeasonNumber;
        Episode = index ?? episode.EpisodeNumber;
        SeasonName = episode.Season.Title;
        EpisodeId = episode.Id;
        ContentRating = episode
            .Tv.CertificationTvs.Where(certificationMovie =>
                RatingClass.IsShownIn(certificationMovie.Certification, country)
            )
            .Select(certificationTv => RatingClass.From(certificationTv.Certification))
            .FirstOrDefault();
    }

    public VideoPlaylistResponseDto(
        Movie movie,
        string playlistType,
        dynamic playlistId,
        string country,
        int? index = null,
        Collection? collection = null
    )
    {
        VideoFile? videoFile = movie.VideoFiles.FirstOrDefault();
        if (videoFile is null)
            return;

        string? logo = movie
            .Images.OrderByDescending(image => image.VoteAverage)
            .FirstOrDefault(image => image.Type == "logo")
            ?.FilePath;
        string baseFolder = $"/{videoFile.Share}{videoFile.Folder}".EncodePath();

        string title = movie.Translations.FirstOrDefault()?.Title ?? movie.Title;
        string? overview = movie.Translations.FirstOrDefault()?.Overview ?? movie.Overview;

        Subs subs = Subtitles(videoFile);
        Id = movie.Id;
        Title = title;
        Description = overview;
        Origin = Info.DeviceId;
        Uuid = movie.Id;
        Duration = videoFile.Duration ?? "0";
        TmdbId = collection?.Id ?? movie.Id;
        VideoType = MediaTypes.MovieMediaType;
        VideoId = videoFile.Id;
        LibraryType = MediaTypes.MovieMediaType;
        PlaylistType = playlistType;
        PlaylistId = playlistId;
        Year = movie.ReleaseDate.ParseYear();
        Image = movie.Backdrop;
        Logo = logo;
        ApplyVideoFile(videoFile, baseFolder, subs);
        ContentRating = movie
            .CertificationMovies.Where(certificationMovie =>
                RatingClass.IsShownIn(certificationMovie.Certification, country)
            )
            .Select(certificationTv => RatingClass.From(certificationTv.Certification))
            .FirstOrDefault();

        if (index is null)
            return;
        SeasonName = "Collection";
        Season = 0;
        Episode = index;
        EpisodeId = movie.Id;
    }

    /// <summary>What playing the file needs: its sources, tracks, metadata and the viewer's progress.</summary>
    private void ApplyVideoFile(VideoFile videoFile, string baseFolder, Subs subs)
    {
        UserData? userData = videoFile.UserData.FirstOrDefault();
        Progress = userData?.LastPlayedDate is not null
            ? new ProgressDto
            {
                Time = userData.Time ?? 0,
                Date = DateTime.Parse(userData.LastPlayedDate),
            }
            : null;
        File = $"{baseFolder}{videoFile.Filename.EncodePath()}";
        Sources =
        [
            new()
            {
                Src = $"{baseFolder}{videoFile.Filename.EncodePath()}",
                Type = videoFile.Filename.Contains(".mp4") ? "video/mp4" : "application/x-mpegURL",
                Languages =
                    JsonConvert
                        .DeserializeObject<string?[]>(videoFile.Languages)
                        ?.Where(lang => lang != null)
                        .ToArray()
                    ?? [],
            },
        ];

        List<VideoTrack> fontsTrack = videoFile.Metadata?.Fonts is { Count: > 0 }
            ? [new() { File = $"{baseFolder}/fonts.json", Kind = "fonts" }]
            : [];

        List<VideoTrack> chaptersTrack = videoFile.Metadata?.ChapterFile
            is { FileSize: > 0 } chaptersFile
            ?
            [
                new()
                {
                    File = $"{baseFolder}{chaptersFile.FileName.EncodePath()}",
                    Kind = "chapters",
                },
            ]
            : [];

        Tracks = NormalizePreviewTracks(
            videoFile
                .Tracks.Select(t => new VideoTrack
                {
                    Label = t.Label,
                    File = $"{baseFolder}{t.File.EncodePath()}",
                    Language = t.Language,
                    Kind = t.Kind,
                })
                .Concat(subs.TextTracks)
                .Concat(fontsTrack)
                .Concat(chaptersTrack)
                .OrderBy(track => track.Language)
                .ToList(),
            videoFile.Metadata,
            baseFolder
        );

        Chapters = videoFile.Metadata?.Chapters ?? [];
        Fonts =
            videoFile
                .Metadata?.Fonts?.Select(font => new IFont
                {
                    FileName = $"{baseFolder}{font.FileName.EncodePath()}",
                    FileHash = font.FileHash,
                    FileSize = font.FileSize,
                })
                .ToList()
            ?? [];

        Audio = videoFile.Metadata?.Audio ?? [];
        Captions = videoFile.Metadata?.Subtitles ?? [];
        Qualities = videoFile.Metadata?.Video ?? [];
    }

    private record Subs
    {
        public List<VideoTrack> TextTracks { get; set; } = [];
    }

    public class Subtitle
    {
        [JsonProperty("language")]
        public string Language { get; set; } = "eng";

        [JsonProperty("type")]
        public string Type { get; set; } = "full";

        [JsonProperty("ext")]
        public string Ext { get; set; } = "vtt";
    }

    private static Subs Subtitles(VideoFile videoFile)
    {
        string baseFolder = $"/{videoFile.Share}{videoFile.Folder}".EncodePath();

        string subtitles = videoFile.Subtitles ?? "[]";
        // Subtitles is a string column on VideoFile that can drift to
        // malformed JSON across schema migrations or partial writes. Treat
        // a parse failure as 'no subtitle tracks' rather than crashing the
        // playlist response.
        List<Subtitle>? subtitleList;
        try
        {
            subtitleList = JsonConvert.DeserializeObject<List<Subtitle>>(subtitles);
        }
        catch (JsonException)
        {
            subtitleList = null;
        }

        List<VideoTrack> textTracks = [];

        foreach (Subtitle sub in subtitleList ?? [])
        {
            string language = sub.Language;
            string type = sub.Type;
            string ext = sub.Ext;

            textTracks.Add(
                new()
                {
                    Label = type,
                    File =
                        $"{baseFolder}/subtitles{(videoFile?.Filename).OrEmpty()
                    .Replace(".mp4", "")
                    .Replace(".m3u8", "")
                    .EncodePath()}.{language}.{type}.{ext}",
                    Language = language,
                    Kind = "subtitles",
                    Ext = ext,
                }
            );
        }

        return new() { TextTracks = textTracks };
    }

    /// <summary>
    /// Presents scrub-bar preview tracks as the two things they are.
    /// <para>A preview is a <c>.webp</c> sheet of frames plus a <c>.vtt</c> naming the
    /// region of that sheet for each moment, and the scanner stores them as
    /// <c>sprite</c> and <c>thumbnails</c> respectively. This used to relabel the
    /// sheet as <c>thumbnails</c> too and then drop all but one, which deleted the
    /// sheet from the payload: the Android and TV players read both kinds and give
    /// up without the image, so previews disappeared wherever a title had both —
    /// most of the library. Relabelling never helped the web player either, since
    /// what it gained was a <c>.webp</c> where it parses cue text.</para>
    /// <para>Kept as a named step rather than inlined: the pairing is the contract
    /// clients depend on, and it is worth stating that both halves go out.</para>
    /// <para>The stored track rows are not trusted over the metadata. A folder
    /// scanned before the preview files were renamed keeps a <c>sprite</c> row
    /// naming a sheet that is no longer on disk and no <c>thumbnails</c> row at
    /// all, and a client with no cue file shows no previews — measured on a real
    /// television against <c>Furious.7.(2015)</c>, whose row said
    /// <c>sprite.webp</c> while the folder held <c>thumbs_320x180.webp</c> and
    /// its <c>.vtt</c>. <see cref="IPreview"/> carries both real names, so where
    /// it has them they replace the pair rather than sit beside it.</para>
    /// </summary>
    private static List<VideoTrack> NormalizePreviewTracks(
        List<VideoTrack> tracks,
        Metadata? metadata,
        string baseFolder
    )
    {
        List<VideoTrack> fromMetadata = PreviewTracksFrom(metadata, baseFolder);
        if (fromMetadata.Count == 0)
            return tracks;

        return
        [
            .. tracks.Where(track => track.Kind is not ("sprite" or "thumbnails")),
            .. fromMetadata,
        ];
    }

    /// <summary>
    /// The sheet and its cue file, named as the scan found them on disk.
    /// </summary>
    private static List<VideoTrack> PreviewTracksFrom(Metadata? metadata, string baseFolder)
    {
        List<VideoTrack> tracks = [];

        foreach (IPreview preview in metadata?.Previews ?? [])
        {
            if (preview is { ImageFileName: { Length: > 0 } sheet, ImageFileSize: > 0 })
                tracks.Add(new() { File = $"{baseFolder}/{sheet.EncodePath()}", Kind = "sprite" });

            if (preview is { TimeFileName: { Length: > 0 } cues, TimeFileSize: > 0 })
                tracks.Add(
                    new() { File = $"{baseFolder}/{cues.EncodePath()}", Kind = "thumbnails" }
                );
        }

        return tracks;
    }
}
