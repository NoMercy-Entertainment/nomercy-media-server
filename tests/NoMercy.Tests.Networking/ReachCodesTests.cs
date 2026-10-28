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

using System.Text;
using System.Text.RegularExpressions;
using NoMercy.NmSystem.Networking;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed partial class ReachCodesTests
{
    [GeneratedRegex("^[A-Za-z0-9_-]{22,64}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApiAcceptedCode();

    [Fact]
    public void AnIssuedCode_MatchesItsOwnPath()
    {
        ReachCodes codes = new();
        string code = codes.Issue();

        Assert.Equal(code, codes.Match(ReachCodes.PathPrefix + code));
    }

    [Fact]
    public void AnIssuedCode_FitsWhatTheApiAccepts()
    {
        // The API validates ^[A-Za-z0-9_-]{22,64}$; a code it refuses is a check that never runs.
        string code = new ReachCodes().Issue();

        Assert.Matches(ApiAcceptedCode(), code);
    }

    [Fact]
    public void ACodeThisServerNeverIssued_DoesNotMatch()
    {
        ReachCodes codes = new();
        codes.Issue();

        // Another device on the same address cannot answer for this server.
        Assert.Null(codes.Match(ReachCodes.PathPrefix + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void ARevokedCode_NoLongerMatches()
    {
        ReachCodes codes = new();
        string code = codes.Issue();

        codes.Revoke(code);

        Assert.Null(codes.Match(ReachCodes.PathPrefix + code));
    }

    [Fact]
    public void AnExpiredCode_NoLongerMatches()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        ReachCodes codes = new(() => now);
        string code = codes.Issue();

        now = now.AddMinutes(3);

        Assert.Null(codes.Match(ReachCodes.PathPrefix + code));
    }

    [Fact]
    public void AQueryString_DoesNotHideTheCode()
    {
        ReachCodes codes = new();
        string code = codes.Issue();

        Assert.Equal(code, codes.Match(ReachCodes.PathPrefix + code + "?t=1"));
    }

    [Fact]
    public void OtherPaths_DoNotMatch()
    {
        ReachCodes codes = new();
        string code = codes.Issue();

        Assert.Null(codes.Match("/status"));
        Assert.Null(codes.Match("/" + code));
        Assert.Null(codes.Match(null));
    }

    [Fact]
    public void TheHttpAnswer_CarriesTheCodeAsItsWholeBody()
    {
        string answer = Encoding.ASCII.GetString(ReachCodes.HttpAnswer("abc"));

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", answer);
        Assert.Contains("Content-Length: 3\r\n", answer);
        Assert.EndsWith("\r\n\r\nabc", answer);
    }
}
