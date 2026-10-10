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

using System.Reflection;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Tests.Common;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// A music library whose files carry no embedded MusicBrainz tags — anything not
/// tagged with Picard, so Bandcamp / Free Music Archive / most CC releases —
/// imported as literally nothing. Every album folder recorded "MusicBrainz release
/// could not be resolved for this folder." and the user saw an empty music library
/// with no error on screen (verified live on v0.1.450: 9 album folders, 145 mp3s,
/// 0 tracks stored, 7 ImportFailure rows).
///
/// The cause was drift between the two import paths: <c>ImportSingles</c> fell back
/// to acoustic fingerprinting for untagged files, while <c>ImportRelease</c> simply
/// skipped them. Both now resolve the release id through the single
/// <c>ResolveReleaseIdAsync</c> helper, which is what these tests pin — the failure
/// mode was two copies of one decision, so the guard is that only one copy exists
/// and both callers use it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AudioImportReleaseResolutionTests
{
    private static string ReadAudioImportJobSource()
    {
        return File.ReadAllText(
            RepoPaths.At("src", "NoMercy.MediaProcessing", "Jobs", "MediaJobs", "AudioImportJob.cs")
        );
    }

    [Fact]
    public void ResolveReleaseIdAsync_IsTheSingleSharedResolutionPoint()
    {
        MethodInfo? resolver = typeof(AudioImportJob).GetMethod(
            "ResolveReleaseIdAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        Assert.NotNull(resolver);
    }

    /// <summary>
    /// Both import paths must call the shared resolver. Two call sites, one per
    /// path — if a future edit re-inlines the tag check in either one, this drops
    /// back to a single occurrence and fails.
    /// </summary>
    [Fact]
    public void BothImportPaths_ResolveThroughTheSharedHelper()
    {
        string source = ReadAudioImportJobSource();

        int callSites = CountOccurrences(
            source,
            "await ResolveReleaseIdAsync(mediaFile, audioTag)"
        );

        Assert.Equal(2, callSites);
    }

    /// <summary>
    /// The specific regression: neither path may decide to skip a file purely
    /// because the embedded MusicBrainz tag is absent. That bare check is what
    /// made untagged albums vanish, and the fingerprint fallback now lives behind
    /// the shared resolver instead.
    /// </summary>
    [Fact]
    public void NeitherImportPath_SkipsUntaggedFilesWithoutFingerprinting()
    {
        string source = ReadAudioImportJobSource();
        string normalized = string.Join(
            ' ',
            source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );

        Assert.DoesNotContain(
            "audioTag.MusicBrainz?.ReleaseId is null || audioTag.MusicBrainz.ReleaseId == Guid.Empty ) continue;",
            normalized
        );
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
