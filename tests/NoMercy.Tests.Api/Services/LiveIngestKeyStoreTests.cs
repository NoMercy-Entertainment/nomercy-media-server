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

using NoMercy.Api.Services;
using NoMercy.Authorization.LiveIngest;
using Xunit;

namespace NoMercy.Tests.Api.Services;

public sealed class LiveIngestKeyStoreTests
{
    [Fact]
    public void Validate_WithMatchingKeyAndPath_ReturnsTrue()
    {
        LiveIngestKeyStore store = new();
        string path = "/01ABC/Anime/Show/Ep.mkv";
        string key = store.Issue(path);

        store.TryValidate(key, path).Should().BeTrue();
    }

    [Fact]
    public void Validate_WithUnknownKey_ReturnsFalse()
    {
        LiveIngestKeyStore store = new();
        store.Issue("/01ABC/Anime/Show/Ep.mkv");

        store.TryValidate("not-a-real-key", "/01ABC/Anime/Show/Ep.mkv").Should().BeFalse();
    }

    [Fact]
    public void Validate_WithDifferentFolder_ReturnsFalse()
    {
        LiveIngestKeyStore store = new();
        string key = store.Issue("/01ABC/Anime/ShowA/Ep.mkv");

        // A key unlocks its own title's folder, not a sibling's.
        store.TryValidate(key, "/01ABC/Anime/ShowB/Ep.mkv").Should().BeFalse();
    }

    [Fact]
    public void Validate_NestedResourceUnderSameFolder_ReturnsTrue()
    {
        LiveIngestKeyStore store = new();
        // Minted for the encoded master; ffmpeg then self-ingests its nested
        // variant playlist, which must validate under the same folder. This is
        // the case the exact-path scope broke (encoded HLS self-ingest 401'd).
        string key = store.Issue("/01ABC/Show/Ep/Ep.The.Title.NoMercy.m3u8");

        store
            .TryValidate(key, "/01ABC/Show/Ep/video_1920x1080_SDR/video_1920x1080_SDR.m3u8")
            .Should()
            .BeTrue();
        store.TryValidate(key, "/01ABC/Show/Ep/audio_eng_aac/audio_eng_aac.m3u8").Should().BeTrue();
        store.TryValidate(key, "/01ABC/Show/Ep/subtitles/eng/full.m3u8").Should().BeTrue();
    }

    [Fact]
    public void Validate_WithPercentEncodedRequestPath_MatchesDecodedBinding()
    {
        LiveIngestKeyStore store = new();
        // Stored decoded, as the serving middleware ultimately sees it.
        string key = store.Issue("/01ABC/Show (2009)/Season 01/Ep.mkv");

        // The source URL percent-encodes segments; an unresolved encoded path
        // must still validate.
        store.TryValidate(key, "/01ABC/Show%20(2009)/Season%2001/Ep.mkv").Should().BeTrue();
    }

    [Fact]
    public void Issue_ProducesDistinctKeys()
    {
        LiveIngestKeyStore store = new();
        string a = store.Issue("/01ABC/a.mkv");
        string b = store.Issue("/01ABC/a.mkv");

        a.Should().NotBe(b);
    }

    [Fact]
    public void RevokeSession_InvalidatesBoundKey()
    {
        LiveIngestKeyStore store = new();
        string path = "/01ABC/Anime/Show/Ep.mkv";
        string key = store.Issue(path);
        store.BindSession(key, "session-1");

        store.RevokeSession("session-1");

        store.TryValidate(key, path).Should().BeFalse();
    }

    [Fact]
    public void RevokeSession_LeavesOtherSessionsKeysAlive()
    {
        LiveIngestKeyStore store = new();
        string keptPath = "/01ABC/kept.mkv";
        string kept = store.Issue(keptPath);
        store.BindSession(kept, "session-keep");

        string dropped = store.Issue("/01ABC/dropped.mkv");
        store.BindSession(dropped, "session-drop");

        store.RevokeSession("session-drop");

        store.TryValidate(kept, keptPath).Should().BeTrue();
    }
}
