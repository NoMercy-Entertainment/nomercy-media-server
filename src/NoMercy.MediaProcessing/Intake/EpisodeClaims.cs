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

namespace NoMercy.MediaProcessing.Intake;

/// <summary>
/// One episode, one encode. Two selected files that resolved to the same media id
/// are two encodes racing for one output directory, and the loser is whichever
/// finishes first — the operator ends up with one of them under a name that
/// describes the other.
/// </summary>
/// <remarks>
/// Which one wins is not "whichever was listed first". The picker sorts by name, and
/// a show's NCED and NCOP both sort ahead of its S01E01, so taking the first arrival
/// would drop the real episode and keep its opening titles. A file that spells out the
/// episode it belongs to is claiming it; one that does not is a guess, and a guess
/// never beats a declaration.
/// </remarks>
public static partial class EpisodeClaims
{
    /// <param name="mediaId">The id the file resolved to; empty when it resolved to nothing.</param>
    /// <returns>The files to import, and the names of the files that lost their claim.</returns>
    public static (List<T> Selected, List<string> Collided) PickOnePerEpisode<T>(
        IEnumerable<T> files,
        Func<T, string> mediaId,
        Func<T, string> path
    )
        where T : class
    {
        List<T> selected = [];
        List<string> collided = [];

        foreach (IGrouping<string, T> claim in files.GroupBy(mediaId))
        {
            if (claim.Key.Length == 0)
            {
                selected.AddRange(claim);
                continue;
            }

            T winner =
                claim.FirstOrDefault(file => DeclaresEpisode(Path.GetFileName(path(file))))
                ?? claim.First();

            selected.Add(winner);
            collided.AddRange(
                claim
                    .Where(file => !ReferenceEquals(file, winner))
                    .Select(file => Path.GetFileName(path(file)))
            );
        }

        return (selected, collided);
    }

    /// <summary>
    /// Whether the file name states which episode it is, rather than leaving it
    /// to be inferred. <c>S01E01</c>, <c>1x01</c> and a bare <c>- 175 -</c>
    /// absolute index all count.
    /// <para>Used only to break a tie between files that resolved to the same
    /// episode. It is not a parser and does not need to be: the question is
    /// which of two candidates said out loud what it belongs to.</para>
    /// </summary>
    public static bool DeclaresEpisode(string fileName) =>
        ExplicitEpisodeMarker().IsMatch(fileName);

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:S\d{1,4}[\s._-]*E\d{1,4}|\d{1,2}x\d{1,3}|-[\s._]*\d{1,4}[\s._]*-)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ExplicitEpisodeMarker();
}
