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
using System.Text.RegularExpressions;
using NoMercy.Setup.Server;
using Match = System.Text.RegularExpressions.Match;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// Requirement (issue #436): the first-run setup page never shows a "Login with
/// NoMercy" button that goes nowhere. The anchor ships hidden with href "#", the
/// silent sign-in check gives up after 3 s (not 10 s), registration progress is a
/// four-row checklist with a running timer, the status detail names the real
/// ceiling (10 minutes), and the page announces the redirect before it jumps.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class SetupPageNeverShowsDeadButtonTests
{
    private static string LoadResource(string filename)
    {
        Assembly assembly = typeof(SetupEndpoints).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(
            $"NoMercy.Setup.Resources.{filename}"
        );
        Assert.NotNull(stream);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("""<a\s+id="btn-login"[^>]*>""")]
    private static partial Regex LoginAnchorRegex();

    [GeneratedRegex(@"(\d+)\s*\);\s*\r?\n\s*window\.addEventListener\(""message""")]
    private static partial Regex SilentTimeoutRegex();

    [GeneratedRegex("""class="check-row""")]
    private static partial Regex CheckRowRegex();

    [Fact]
    public void LoginAnchor_IsHiddenInHtml_WhileHrefIsPlaceholder()
    {
        string html = LoadResource("setup.html");

        Match anchor = LoginAnchorRegex().Match(html);
        Assert.True(anchor.Success, "setup.html has no #btn-login anchor");
        Assert.Contains("href=\"#\"", anchor.Value);
        Assert.Contains("qr-hidden", anchor.Value);
    }

    [Fact]
    public void LoginAnchor_IsRevealedOnlyWhereHrefIsSet()
    {
        string js = LoadResource("setup.js");

        // The only place the button becomes visible is applyLoginMode, which also sets
        // the real href on the same branch, before the reveal.
        int reveal = js.IndexOf(
            "el(\"btn-login\").classList.remove(\"qr-hidden\")",
            StringComparison.Ordinal
        );
        int hrefSet = js.IndexOf(
            "el(\"btn-login\").href = buildAuthUrl()",
            StringComparison.Ordinal
        );

        Assert.True(reveal >= 0, "setup.js never reveals #btn-login");
        Assert.True(hrefSet >= 0, "setup.js never sets the real href");
        Assert.True(hrefSet < reveal, "the href must be set before the button is revealed");
        Assert.DoesNotContain(
            "el(\"btn-login\").classList.toggle(\"qr-hidden\", !browserLoginAllowed)",
            js
        );
    }

    [Fact]
    public void Html_ShowsSignInCheckPlaceholder_WhereTheButtonWillBe()
    {
        string html = LoadResource("setup.html");

        Assert.Contains("id=\"login-checking\"", html);
        Assert.Contains("Checking if you're already signed in...", html);
    }

    [Fact]
    public void SilentSsoTimeout_IsThreeSeconds()
    {
        string js = LoadResource("setup.js");

        Match timeout = SilentTimeoutRegex().Match(js);
        Assert.True(timeout.Success, "could not find the silent SSO setTimeout");
        Assert.Equal("3000", timeout.Groups[1].Value);
        Assert.DoesNotContain("10000", js);
    }

    [Fact]
    public void Html_HasFourChecklistRows_AndATimer()
    {
        string html = LoadResource("setup.html");

        Assert.Contains("data-step=\"signin\"", html);
        Assert.Contains("data-step=\"connect\"", html);
        Assert.Contains("data-step=\"address\"", html);
        Assert.Contains("data-step=\"finish\"", html);
        Assert.Equal(4, CheckRowRegex().Matches(html).Count);
        Assert.Contains("id=\"progress-timer\"", html);
    }

    [Fact]
    public void Js_MapsEveryServerPhase_ToAChecklistState()
    {
        string js = LoadResource("setup.js");

        // The page keeps no label table: the words come from the server's one
        // SetupPhaseWords table as data.label / data.detail (issue #438).
        Assert.DoesNotContain("var phases = {", js);
        Assert.Contains("data.label", js);
        Assert.Contains("var checklistByPhase = {", js);
        foreach (
            string phase in new[]
            {
                "Unauthenticated",
                "Authenticating",
                "Authenticated",
                "Registering",
                "Registered",
                "CertificateAcquired",
                "Failed",
                "Complete",
            }
        )
        {
            Assert.Contains($"\"{phase}\":", js);
        }
    }

    [Fact]
    public void Js_AnnouncesTheRedirect_BeforeItJumps()
    {
        string js = LoadResource("setup.js");

        Assert.Contains("Opening the NoMercy app in ", js);
    }
}
