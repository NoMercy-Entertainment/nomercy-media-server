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

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Providers.TMDB.Models.Episode;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.MediaProcessing.Common;

public static partial class FileNameParsers
{
    [GeneratedRegex("^The[\\s]*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingTheRegex();

    [GeneratedRegex("^An[\\s]{1,}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingAnRegex();

    [GeneratedRegex("^A[\\s]{1,}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingARegex();

    [GeneratedRegex(@":\s|\sand\sthe", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TitleSeparatorRegex();

    private static string Pad(int number, int width)
    {
        return number.ToString().PadLeft(width, '0');
    }

    public static string CreateBaseFolder(TmdbTvShow show)
    {
        return "/"
            + string.Concat([show.Name.CleanFileName(), ".(", show.FirstAirDate.ParseYear(), ")"])
                .CleanFileName();
    }

    public static string CreateBaseFolder(Tv show)
    {
        return "/"
            + string.Concat([show.Title.CleanFileName(), ".(", show.FirstAirDate.ParseYear(), ")"])
                .CleanFileName();
    }

    public static string CreateBaseFolder(TmdbMovieDetails tmdbMovie)
    {
        return "/"
            + string.Concat([tmdbMovie.Title, ".(", tmdbMovie.ReleaseDate.ParseYear(), ")"])
                .CleanFileName();
    }

    public static string CreateBaseFolder(Movie movie)
    {
        return "/"
            + string.Concat([movie.Title, ".(", movie.ReleaseDate.ParseYear(), ")"]).CleanFileName();
    }

    public static string CreateEpisodeFolder(TmdbEpisode data, TmdbTvShow show)
    {
        return string.Concat([show.Name, "S", Pad(data.SeasonNumber, 2), "E", Pad(data.EpisodeNumber, 2)]
            )
            .CleanFileName();
    }

    public static string CreateTitleSort(string title, DateTime? date = null)
    {
        // Step 1: Capitalize the first letter of the title
        title = char.ToUpper(title[0]) + title[1..];

        // Step 2: Remove leading "The", "An", and "A" from the title
        title = LeadingTheRegex().Replace(title, "");
        title = LeadingAnRegex().Replace(title, "");
        title = LeadingARegex().Replace(title, "");

        // Step 3: Replace ": " and " and the" with the parsed year (if available) or "."
        string replacement = date != null ? $".{date.ParseYear()}." : ".";
        title = TitleSeparatorRegex().Replace(title, replacement);

        // Step 4: Replace all "." with " "
        title = title.Replace(".", " ");

        // Step 5: Convert the title to lowercase
        title = title.ToLower();

        return title.CleanFileName();
    }

    public static string CreateMediaFolder(Library library, TmdbMovieDetails tmdbMovie)
    {
        string? baseFolder = library.FolderLibraries.FirstOrDefault()?.Folder.Path;
        if (baseFolder is null)
            throw new InvalidOperationException(
                $"Library '{library.Title}' has no folders assigned — cannot determine the destination folder for movie '{tmdbMovie.Title}'."
            );

        return string.Concat(baseFolder, "/", CreateBaseFolder(tmdbMovie)).CleanFileName();
    }

    public static string CreateMediaFolder(Library library, TmdbTvShow tmdbTv)
    {
        string? baseFolder = library.FolderLibraries.FirstOrDefault()?.Folder.Path;
        if (baseFolder is null)
            throw new InvalidOperationException(
                $"Library '{library.Title}' has no folders assigned — cannot determine the destination folder for show '{tmdbTv.Name}'."
            );

        return string.Concat(baseFolder, "/", CreateBaseFolder(tmdbTv)).CleanFileName();
    }

    public static string CreateFileName(TmdbMovieDetails tmdbMovie)
    {
        return string.Concat([tmdbMovie.Title, ".(", tmdbMovie.ReleaseDate.ParseYear(), ").NoMercy"])
            .CleanFileName();
    }

    public static string CreateFileName(TmdbEpisode tmdbEpisode, TmdbTvShow tmdbTvShow)
    {
        return string.Concat([tmdbTvShow.Name, ".", Pad(tmdbEpisode.SeasonNumber, 2), "E", Pad(tmdbEpisode.EpisodeNumber, 2), ".", tmdbEpisode.Name, ".NoMercy"]
            )
            .CleanFileName();
    }

    public static string? CreateRootFolderName(string folder)
    {
        using MediaContext context = new();
        return context
            .Libraries.Include(l => l.FolderLibraries)
                .ThenInclude(folderLibrary => folderLibrary.Folder)
            .SelectMany(l => l.FolderLibraries)
            .FirstOrDefault(m => folder.Contains(m.Folder.Path))
            ?.Folder.Path;
    }

    public static string CreateBaseFolder(MusicBrainzRecordingAppends music)
    {
        return string.Concat(music.ArtistCredit[0].Name[0], "/", music.ArtistCredit[0].Name)
            .CleanFileName();
    }
}
