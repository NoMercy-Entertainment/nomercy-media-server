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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Database.Music;

/// <summary>
/// The on-disk layout Stoney's library already uses, ported from his Picard naming
/// script (Bob Swift / rdswift, with the "Additional Artists Variables" plugin).
/// <para>
/// A library that is already filed this way is the specification: the server has to
/// reproduce it exactly or an import renames real files into a second, conflicting
/// convention. Every rule here traces to a line in that script rather than to taste.
/// </para>
/// <para>
/// His settings disable the label, catalogue number, disambiguation and second-year
/// suffixes (the enabling values sit inside a <c>$noop</c> block), so an album folder
/// is exactly <c>[year] Album</c>.
/// </para>
/// </summary>
public static partial class PicardNaming
{
    public const string VariousArtistId = "89ad4ac3-39f7-470e-963a-56509c546377";
    public const string UnknownArtistId = "125ec42a-7229-4250-afc5-e057484327fe";

    private const string VariousArtistFolder = "[Various Artists]";
    private const string UnknownArtistFolder = "[Unknown Artist]";
    private const string ClassicalFolder = "[Classical]";
    private const string SoundtrackFolder = "[Soundtracks]";
    private const string SinglesFolder = "[Singles]";
    private const string OtherFolder = "[Other]";

    private const int TitleMaxLength = 128;
    private const int DiscPadMinLength = 1;
    private const int TrackPadMinLength = 2;

    /// <summary>
    /// Runs of <c>? * : \ _</c> collapse to a single underscore, matching the script's
    /// closing <c>$rreplace</c> exactly so a title carrying a colon lands identically to
    /// the files already on disk. <c>" &lt; &gt; |</c> are folded alongside them — not part
    /// of the original script, but reserved on Windows regardless, and left unfolded here
    /// a title carrying one (or the curly-quote fold in <see cref="FoldUnsafeUnicode"/>
    /// producing a straight <c>"</c>) fails CreateDirectory outright rather than just
    /// looking different from Picard's own output.
    /// </summary>
    [GeneratedRegex("[?*:\\\\_\"<>|]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnsafeRun();

    [GeneratedRegex(@"\.(?=/)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingFolderDot();

    [GeneratedRegex(@"^(the|a|an)\s+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingArticle();

    public static string BuildPath(MusicNamingContext context) =>
        $"{BuildDirectory(context)}/{BuildFileName(context)}";

    /// <summary>
    /// The folder the release files into, relative to the library root. Separate from the
    /// full path because the encoder needs somewhere to put its output directory, and
    /// that destination is a folder rather than a file.
    /// </summary>
    public static string BuildDirectory(MusicNamingContext context)
    {
        string album = Truncate(Fallback(context.AlbumName, "[Unknown Album]"));
        string year = $"[{Year(context.Year)}]";

        return context.AlbumType switch
        {
            MusicAlbumType.Classical => $"{ClassicalFolder}/{year} {album}",
            MusicAlbumType.Soundtrack => $"{SoundtrackFolder}/{year} {album}",
            MusicAlbumType.Other => $"{OtherFolder}/{year} {album}",
            MusicAlbumType.Single => SingleArtistPath(context),
            _ => StandardPath(context, year, album),
        };
    }

    private static string StandardPath(MusicNamingContext context, string year, string album)
    {
        if (
            string.Equals(
                context.AlbumArtistId,
                VariousArtistId,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return $"{VariousArtistFolder}/{year} {album}";

        if (
            string.IsNullOrWhiteSpace(context.AlbumArtistId)
            || string.Equals(
                context.AlbumArtistId,
                UnknownArtistId,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return $"{UnknownArtistFolder}/{year} {album}";

        string artist = Fallback(
            context.AlbumArtistPrimary ?? context.AlbumArtistSort,
            UnknownArtistFolder
        );
        return $"{Initial(artist)}/{artist}/{year} {album}";
    }

    private static string SingleArtistPath(MusicNamingContext context)
    {
        string artist = Fallback(
            context.AlbumArtistPrimary ?? context.AlbumArtistSort,
            UnknownArtistFolder
        );
        return $"{Initial(artist)}/{artist}/{SinglesFolder}";
    }

    public static string BuildFileName(MusicNamingContext context)
    {
        string title = Truncate(Fallback(context.TrackTitle, "[Unknown Title]"));
        string number = TrackNumber(context);
        string feat = Feat(context);

        string name = context.AlbumType switch
        {
            MusicAlbumType.Classical => $"{number}{feat} {title}",
            MusicAlbumType.Other => $"{number} {title}",
            MusicAlbumType.Single => $"[{Year(context.Year)}] {title}{feat}",
            _ => $"{number} {title}{feat}",
        };

        return Truncate(name);
    }

    /// <summary>
    /// The disc prefix appears only on multi-disc releases. Both numbers pad to the digit
    /// count of their total, never below the configured minimum — a 100-track release
    /// therefore pads to three, which is what keeps a directory listing in play order.
    /// </summary>
    private static string TrackNumber(MusicNamingContext context)
    {
        int trackWidth = Math.Max(context.TotalTracks.ToString().Length, TrackPadMinLength);
        string track = context.TrackNumber.ToString().PadLeft(trackWidth, '0');

        if (context.TotalDiscs <= 1)
            return track;

        int discWidth = Math.Max(context.TotalDiscs.ToString().Length, DiscPadMinLength);
        return $"{context.DiscNumber.ToString().PadLeft(discWidth, '0')}-{track}";
    }

    /// <summary>
    /// A track whose credited artist is the album's artist needs no suffix; the guests on
    /// it do. When the track artist is someone else entirely the whole credit is shown
    /// instead, which is what makes a compilation readable in a file browser.
    /// </summary>
    private static string Feat(MusicNamingContext context)
    {
        if (context.AlbumType is MusicAlbumType.Soundtrack or MusicAlbumType.Other)
            return Wrap(context.TrackArtistsCredited);

        if (context.AlbumType is MusicAlbumType.Classical)
            return Wrap(context.TrackArtistPrimary);

        bool sameArtist = string.Equals(
            Comparable(context.AlbumArtistPrimary),
            Comparable(context.TrackArtistPrimary),
            StringComparison.OrdinalIgnoreCase
        );

        if (!sameArtist)
            return Wrap(context.TrackArtistsCredited);

        return string.IsNullOrWhiteSpace(context.TrackArtistsAdditional)
            ? string.Empty
            : $" [feat. {context.TrackArtistsAdditional}]";
    }

    private static string Wrap(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : $" [{value}]";

    private static string Comparable(string? artist) =>
        LeadingArticle().Replace((artist ?? string.Empty).Trim(), string.Empty).ToLowerInvariant();

    /// <summary>
    /// The script's <c>$firstalphachar</c> looks at the FIRST character only and falls
    /// back to "#" when it is not a letter — so "2Pac" files under "#", not under the "P"
    /// a search for the first letter anywhere would find.
    /// </summary>
    private static string Initial(string? artistSort)
    {
        string name = (artistSort ?? string.Empty).TrimStart();

        return name.Length > 0 && char.IsLetter(name[0])
            ? char.ToUpperInvariant(name[0]).ToString()
            : "#";
    }

    private static string Year(int? year) =>
        year is null or <= 0 ? "0000" : year.Value.ToString("D4");

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Truncate(string value) =>
        value.Length <= TitleMaxLength ? value : $"{value[..(TitleMaxLength - 3)]}...";

    /// <summary>
    /// Applied to the joined path, never to a fragment: the script sanitizes once at the
    /// end, so a separator produced by one rule is not mistaken for an unsafe character
    /// belonging to another.
    /// </summary>
    /// <remarks>
    /// Windows drops a folder's trailing dot, so "[2001] Melody A.M." was created as a
    /// second "[2001] Melody A.M" beside the album's "[2001] Melody A.M_". Picard writes
    /// that last dot as "_" ("M.I.A.M.I_", "On a Day Like This.._" in the library).
    /// </remarks>
    public static string Sanitize(string pathAndFileName) =>
        TrailingFolderDot()
            .Replace(UnsafeRun().Replace(FoldUnsafeUnicode(pathAndFileName), "_"), "_");

    /// <summary>
    /// A deliberate deviation from the byte-exact Picard reproduction: any character
    /// outside ASCII reproducibly fails to create a directory on Stoney's NAS — the
    /// export is reached through the Windows-mounted NFS client, and confirmed
    /// identically for é ñ ü à ö ç í ó ø å and the curly apostrophe ’ across raw Win32
    /// CreateDirectoryW, .NET's Directory.CreateDirectory, and PowerShell's New-Item,
    /// and across both soft and hard NFS mount modes. It isn't a transient fault a
    /// retry can outlast: a directory that reports created with a non-ASCII character
    /// in its name is gone again within seconds, server-side, every time — the same
    /// data reached over SMB persists it correctly, so this is a client-protocol
    /// defect, not a filesystem limit. A path this exists to build has to actually
    /// persist, so every accented/typographic character folds to its closest ASCII
    /// form here rather than just the one Picard's own typography happens to emit.
    /// </summary>
    /// <summary>
    /// Public: <see cref="MusicEncodeJob.AlbumOutputDirectory"/> repairs a legacy row's
    /// stored path with this exact fold, so a directory built at dispatch time and one
    /// rebuilt on read never diverge.
    /// </summary>
    public static string FoldUnsafeUnicode(string value)
    {
        // Two tiers, not one growing list: NFD-decompose below is the generative
        // mechanism and covers every base+diacritic letter (é ñ ü à ö ç í ó å, any
        // future one) with no new entries ever needed. This map is the closed
        // remainder — characters Unicode defines as distinct letters/punctuation
        // rather than base+diacritic, so NFD cannot decompose them by definition.
        string quotesFolded = value
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('ø', 'o')
            .Replace('Ø', 'O')
            .Replace('đ', 'd')
            .Replace('Đ', 'D')
            .Replace('ł', 'l')
            .Replace('Ł', 'L')
            .Replace('ß', 's');

        string decomposed = quotesFolded.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
