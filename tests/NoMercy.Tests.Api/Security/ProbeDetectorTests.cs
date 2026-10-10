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

using NoMercy.Api.Security;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class ProbeDetectorTests
{
    [Theory]
    [InlineData("/wp-content/plugins/hellopress/wp_filemanager.php")]
    [InlineData("/9WOLF.php")]
    [InlineData("/wp-login.php")]
    [InlineData("/phpmyadmin/index.php")]
    [InlineData("/vendor/phpunit/phpunit/src/Util/PHP/eval-stdin.php")]
    [InlineData("/.env")]
    [InlineData("/.git/config")]
    [InlineData("/.aws/credentials")]
    [InlineData("/cgi-bin/luci")]
    [InlineData("/xmlrpc.php")]
    [InlineData("/actuator/env")]
    [InlineData("/boaform/admin/formLogin")]
    public void Classify_KnownExploitPath_IsKnownProbe(string path)
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new(path, false, 401, false));

        verdict.Should().Be(ProbeVerdict.KnownProbe);
    }

    [Fact]
    public void Classify_KnownExploitPath_StillProbeWhenAuthenticatedAndRouted()
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new("/wp-login.php", true, 200, true));

        verdict.Should().Be(ProbeVerdict.KnownProbe);
    }

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        ProbeVerdict verdict = ProbeDetector.Classify(
            new("/WP-ADMIN/Setup-Config.PHP", false, 401, false)
        );

        verdict.Should().Be(ProbeVerdict.KnownProbe);
    }

    [Fact]
    public void Classify_UnroutedRejectedAnonymousRequest_IsSuspicious()
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new("/admin/console", false, 401, false));

        verdict.Should().Be(ProbeVerdict.Suspicious);
    }

    [Fact]
    public void Classify_RoutedEndpoint_IsCleanEvenWhenRejected()
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new("/api/v1/movies", true, 401, false));

        verdict.Should().Be(ProbeVerdict.Clean);
    }

    [Fact]
    public void Classify_AuthenticatedCaller_IsCleanEvenWhenUnrouted()
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new("/typo", false, 404, true));

        verdict.Should().Be(ProbeVerdict.Clean);
    }

    [Fact]
    public void Classify_SuccessfulUnroutedRequest_IsClean()
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new("/some/static/file", false, 200, false));

        verdict.Should().Be(ProbeVerdict.Clean);
    }

    [Theory]
    [InlineData("/assets/app-a91f.js")]
    [InlineData("/assets/app-a91f.css")]
    [InlineData("/assets/app.js.map")]
    [InlineData("/fonts/inter.woff2")]
    [InlineData("/images/poster.jpg")]
    [InlineData("/favicon.ico")]
    [InlineData("/manifest.webmanifest")]
    public void Classify_MissingStaticAsset_IsClean(string path)
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new(path, false, 404, false));

        verdict.Should().Be(ProbeVerdict.Clean);
    }

    // Media is served by the static-file middleware, so it never matches a
    // controller. Once a token expires mid-episode the player retries segments
    // anonymously and every retry looks exactly like an unrouted refusal.
    [Theory]
    [InlineData("/01HQ8/Movies/Sintel/Sintel.m3u8")]
    [InlineData("/01HQ8/Movies/Sintel/video_1080p/segment_042.ts")]
    [InlineData("/01HQ8/Movies/Sintel/video_1080p/segment_042.m4s")]
    [InlineData("/01HQ8/Movies/Sintel/Sintel.mp4")]
    [InlineData("/01HQ8/Movies/Sintel/subtitles/eng.vtt")]
    [InlineData("/01HQ8/Music/Derek Clegg/track.flac")]
    [InlineData("/01HQ8/Movies/Sintel/hls.key")]
    public void Classify_ExpiredTokenRetryingMedia_IsCleanAndNeverBansTheViewer(string path)
    {
        ProbeVerdict verdict = ProbeDetector.Classify(new(path, false, 401, false));

        verdict.Should().Be(ProbeVerdict.Clean);
    }

    [Fact]
    public void Classify_ExploitPathWithAServedExtension_IsStillAProbe()
    {
        ProbeVerdict verdict = ProbeDetector.Classify(
            new("/wp-content/uploads/shell.png", false, 401, false)
        );

        verdict.Should().Be(ProbeVerdict.KnownProbe);
    }

    [Fact]
    public void Weight_ScoresAKnownProbeFarAboveASuspiciousRequest()
    {
        ProbeDetector.Weight(ProbeVerdict.KnownProbe).Should().Be(5);
        ProbeDetector.Weight(ProbeVerdict.Suspicious).Should().Be(1);
        ProbeDetector.Weight(ProbeVerdict.Clean).Should().Be(0);
    }
}
