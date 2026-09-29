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
using NoMercy.NmSystem.Configuration;

namespace NoMercy.NmSystem.SystemCalls;

/// <summary>
/// Picks the newest server release a channel offers, from the release list
/// GitHub returns. How a release shows its channel is set by the release
/// workflows: a nightly is a prerelease, promote-release.yml marks a beta with
/// <see cref="BetaMarker"/> and makes a stable a full release, and
/// retract-release.yml prepends a RETRACTED notice. Pure, so the policy is
/// tested without the network.
/// </summary>
public static partial class ReleaseChannelSelector
{
    public const string MediaServerReleaseListUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-media-server/releases?per_page=30";

    private const string BetaMarker = "<!-- nomercy-channel: beta -->";
    private const string RetractedNotice = "**RETRACTED ";

    public static T? Select<T>(
        IEnumerable<T> releases,
        Func<T, ReleaseSummary> summarize,
        ReleaseChannel channel
    )
        where T : class
    {
        T? newest = null;
        Version? newestVersion = null;

        foreach (T release in releases)
        {
            ReleaseSummary summary = summarize(release);

            if (
                !IsOffered(summary, channel)
                || !TryParseVersion(summary.TagName, out Version version)
            )
                continue;

            if (newestVersion is null || version > newestVersion)
            {
                newest = release;
                newestVersion = version;
            }
        }

        return newest;
    }

    public static bool TryParse(string value, out ReleaseChannel channel)
    {
        // Enum.TryParse also accepts any number, which would store a channel
        // that does not exist.
        channel = ReleaseChannel.Stable;
        return !string.IsNullOrEmpty(value)
            && !char.IsDigit(value[0])
            && Enum.TryParse(value, true, out channel)
            && Enum.IsDefined(channel);
    }

    private static bool IsOffered(ReleaseSummary release, ReleaseChannel channel)
    {
        // GitHub sends "body": null for a release with no notes, and the JSON
        // deserializer writes that null over the property's default.
        string body = release.Body ?? string.Empty;

        if (release.Draft || body.Contains(RetractedNotice, StringComparison.Ordinal))
            return false;

        return channel switch
        {
            ReleaseChannel.Stable => !release.Prerelease,
            ReleaseChannel.Beta => !release.Prerelease
                || body.Contains(BetaMarker, StringComparison.Ordinal),
            ReleaseChannel.Nightly => true,
            _ => false,
        };
    }

    private static bool TryParseVersion(string tagName, out Version version)
    {
        version = new();
        Match match = PlainVersionTag().Match(tagName ?? string.Empty);
        return match.Success && Version.TryParse(match.Groups[1].Value, out version!);
    }

    [GeneratedRegex(@"^v?(\d+\.\d+\.\d+)$")]
    private static partial Regex PlainVersionTag();
}
