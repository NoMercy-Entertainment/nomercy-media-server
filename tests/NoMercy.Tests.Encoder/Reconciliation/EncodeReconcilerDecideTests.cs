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

using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Reconciliation;

namespace NoMercy.Tests.Encoder.Reconciliation;

/// <summary>
/// Unit tests for the reconciliation DECISION only — no filesystem, no
/// database. Every scenario hands a hand-built <see cref="ExistingOutputSnapshot"/>
/// straight to <see cref="EncodeReconciler.Decide"/> and asserts the verdict.
/// </summary>
public class EncodeReconcilerDecideTests
{
    private readonly EncodeReconciler _reconciler = new();

    [Fact]
    public void Decide_ReturnsSkip_WhenFingerprintMatchesAndEveryOutputIsPresentAndValid()
    {
        EncodingProfile profile = MakeHlsProfile();
        string fingerprint = ProfileFingerprint.Compute(profile);
        ExistingOutputSnapshot existing = new(
            fingerprint,
            AllPresentFiles(),
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Skip);
        decision.MissingKinds.Should().BeEmpty();
        decision.NeedsSubtitleOcr.Should().BeFalse();
    }

    [Fact]
    public void Decide_ReturnsPartialOcrOnly_WhenFingerprintMatchesButBitmapOcrVttIsMissing_FrierenRegression()
    {
        // This is the exact regression the reconciler exists to fix: video,
        // audio, and declared subtitles are all valid and on-profile — only
        // the bitmap-subtitle OCR sidecar is missing. Re-dispatching must
        // never re-run Build/Execute for this file; MissingKinds must stay
        // empty and only NeedsSubtitleOcr should be set.
        EncodingProfile profile = MakeHlsProfile();
        string fingerprint = ProfileFingerprint.Compute(profile);
        ExistingOutputSnapshot existing = new(
            fingerprint,
            AllPresentFiles(),
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 1, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().BeEmpty("nothing in the decomposed plan is missing");
        decision.NeedsSubtitleOcr.Should().BeTrue();
    }

    [Fact]
    public void Decide_TreatsTheRealSpriteFilenameAsPresent_WhenThumbnailsComeFromTheGenerateSpriteVttDefault()
    {
        // The preset leaves Thumbnails null and inherits GenerateSpriteVtt, which
        // is how every HLS preset here gets its sprite. The sprite lands as
        // thumbs_{W}x{H}.webp at the default tile width, so an output carrying
        // that pair is complete and must not re-run the thumbnail pass.
        EncodingProfile profile = MakeHlsProfile();
        profile.Thumbnails.Should().BeNull("this preset relies on the sprite default");

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: false,
                BitmapSubtitleStreamCount: 0,
                new(ProfileFingerprint.Compute(profile), AllPresentFiles(), ValidOcrSidecarCount: 0)
            )
        );

        decision.MissingKinds.Should().NotContain(EncodeTaskKind.Thumbnails);
        decision.Action.Should().Be(ReconciliationAction.Skip);
    }

    [Fact]
    public void Decide_ReturnsPartialThumbnails_WhenTheSpriteIsMissingUnderTheGenerateSpriteVttDefault()
    {
        // Counterpart to the test above: same preset, sprite absent. A preset that
        // never sets Thumbnails still wants one, so the gap must be topped up.
        EncodingProfile profile = MakeHlsProfile();
        List<ExistingOutputEntry> withoutSprite =
        [
            .. AllPresentFiles()
                .Where(f => !f.RelativePath.StartsWith("thumbs_", StringComparison.Ordinal)),
        ];

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: false,
                BitmapSubtitleStreamCount: 0,
                new(ProfileFingerprint.Compute(profile), withoutSprite, ValidOcrSidecarCount: 0)
            )
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().Contain(EncodeTaskKind.Thumbnails);
        decision.MissingKinds.Should().NotContain(EncodeTaskKind.Video);
    }

    [Fact]
    public void Decide_ReturnsPartialThumbnails_WhenTheSpriteWasRenderedAtANarrowerTileThanTheProfileWants()
    {
        // A title encoded when the tile was 160 wide. Everything is present, so
        // presence alone would call this complete and leave a preview that is
        // blown up several times over on a television. The sheet is a sidecar:
        // the answer is one sprite pass, never a re-encode.
        EncodingProfile profile = MakeHlsProfile();
        List<ExistingOutputEntry> withOldSprite =
        [
            .. AllPresentFiles()
                .Where(f => !f.RelativePath.StartsWith("thumbs_", StringComparison.Ordinal)),
            new("thumbs_160x90.webp", 298_000),
            new("thumbs_160x90.vtt", 4_000),
        ];

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: false,
                BitmapSubtitleStreamCount: 0,
                new(ProfileFingerprint.Compute(profile), withOldSprite, ValidOcrSidecarCount: 0)
            )
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().Contain(EncodeTaskKind.Thumbnails);
        decision
            .MissingKinds.Should()
            .NotContain(EncodeTaskKind.Video, "a preview upgrade must never re-encode video");
        decision.MissingKinds.Should().NotContain(EncodeTaskKind.Audio);
    }

    [Fact]
    public void Decide_ReturnsSkip_WhenAFingerprintFromAnOlderCoverageIsOnRecord_UpgradeSafety()
    {
        // Every output produced before the fingerprint narrowed to encoded
        // streams carries a bare hash. It measured something else, so it cannot
        // be compared — and reading "cannot compare" as "profile changed" would
        // re-encode an operator's whole library the moment they upgrade.
        EncodingProfile profile = MakeHlsProfile();

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: false,
                BitmapSubtitleStreamCount: 0,
                new(
                    "9f2c7a1e6b40d3518c7de9a0f4b28c6d15e3a7920b4c8de6f1a35c9207b8e4d6a",
                    AllPresentFiles(),
                    ValidOcrSidecarCount: 0
                )
            )
        );

        decision.Action.Should().Be(ReconciliationAction.Skip);
    }

    [Fact]
    public void Decide_ReturnsPartialSubtitleOnly_WhenTheDeclaredSubtitleTrackIsMissing()
    {
        EncodingProfile profile = MakeHlsProfile();
        string fingerprint = ProfileFingerprint.Compute(profile);
        List<ExistingOutputEntry> files =
        [
            .. AllPresentFiles()
                .Where(f => !f.RelativePath.StartsWith("subtitles/", StringComparison.Ordinal)),
        ];
        ExistingOutputSnapshot existing = new(fingerprint, files, ValidOcrSidecarCount: 0);

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().ContainSingle().Which.Should().Be(EncodeTaskKind.Subtitle);
        decision.NeedsSubtitleOcr.Should().BeFalse();
    }

    [Fact]
    public void Decide_ReturnsPartialAudioOnly_WhenTheAudioRenditionIsMissing()
    {
        // The video-present / audio-missing case: deleting an audio_* rendition
        // must top up only the audio, exactly as a missing subtitle or thumbnail
        // does — never a full re-encode of the video that is already on disk.
        // Confirmed live against a real special (0 video / 1 audio bundle); pinned
        // here so that decomposed-bundle path cannot regress unseen.
        EncodingProfile profile = MakeHlsProfile();
        string fingerprint = ProfileFingerprint.Compute(profile);
        List<ExistingOutputEntry> files =
        [
            .. AllPresentFiles()
                .Where(f => !f.RelativePath.StartsWith("audio_", StringComparison.Ordinal)),
        ];
        ExistingOutputSnapshot existing = new(fingerprint, files, ValidOcrSidecarCount: 0);

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().ContainSingle().Which.Should().Be(EncodeTaskKind.Audio);
        decision.MissingKinds.Should().NotContain(EncodeTaskKind.Video);
    }

    [Fact]
    public void Decide_ReturnsFull_WhenTheProfileFingerprintDiffers()
    {
        // The preset was edited in place: same id, different settings. The stored
        // fingerprint is a real one — comparable, and different — which is what
        // separates this from an unreadable fingerprint that must be let through.
        EncodingProfile profile = MakeHlsProfile();
        EncodingProfile asItWasEncoded = profile with { Container = Container.HlsTs };

        ExistingOutputSnapshot existing = new(
            ProfileFingerprint.Compute(asItWasEncoded),
            AllPresentFiles(),
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Full);
        decision.MissingKinds.Should().BeEmpty();
    }

    [Fact]
    public void Decide_ReturnsSkip_WhenNoFingerprintIsOnRecordButEveryOutputIsPresent_UpgradeSafety()
    {
        // Every real output on disk today predates fingerprinting. Treating
        // "no fingerprint" as "profile changed" would force a full re-encode
        // of an operator's entire library the moment the server upgrades —
        // unacceptable for a self-hosted product.
        EncodingProfile profile = MakeHlsProfile();
        ExistingOutputSnapshot existing = new(null, AllPresentFiles(), ValidOcrSidecarCount: 0);

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Skip);
    }

    [Fact]
    public void Decide_ReturnsPartialOcrOnly_WhenNoFingerprintAndBitmapOcrVttIsMissing()
    {
        EncodingProfile profile = MakeHlsProfile();
        ExistingOutputSnapshot existing = new(null, AllPresentFiles(), ValidOcrSidecarCount: 0);

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 2, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().BeEmpty();
        decision.NeedsSubtitleOcr.Should().BeTrue();
    }

    [Fact]
    public void Decide_TreatsAZeroByteOutputAsInvalid_AndRePairsOnlyThatKind()
    {
        EncodingProfile profile = MakeHlsProfile();
        string fingerprint = ProfileFingerprint.Compute(profile);
        List<ExistingOutputEntry> files =
        [
            .. AllPresentFiles()
                .Select(f =>
                    f.RelativePath.StartsWith("audio_", StringComparison.Ordinal)
                        ? f with
                        {
                            SizeBytes = 0,
                        }
                        : f
                ),
        ];
        ExistingOutputSnapshot existing = new(fingerprint, files, ValidOcrSidecarCount: 0);

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().ContainSingle().Which.Should().Be(EncodeTaskKind.Audio);
    }

    [Fact]
    public void Decide_ReturnsFull_WhenForced_RegardlessOfFingerprintOrWhatIsPresent()
    {
        EncodingProfile profile = MakeHlsProfile();
        string fingerprint = ProfileFingerprint.Compute(profile);
        ExistingOutputSnapshot existing = new(
            fingerprint,
            AllPresentFiles(),
            ValidOcrSidecarCount: 5
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: false,
                BitmapSubtitleStreamCount: 0,
                existing,
                Force: true
            )
        );

        decision.Action.Should().Be(ReconciliationAction.Full);
    }

    [Fact]
    public void Decide_ReturnsFull_WhenTheMasterPlaylistIsMissing_EvenIfTheFingerprintMatches()
    {
        EncodingProfile profile = MakeHlsProfile();
        string fingerprint = ProfileFingerprint.Compute(profile);
        List<ExistingOutputEntry> files =
        [
            .. AllPresentFiles()
                .Where(f =>
                    f.RelativePath.Contains('/')
                    || !f.RelativePath.EndsWith(".m3u8", StringComparison.Ordinal)
                ),
        ];
        ExistingOutputSnapshot existing = new(fingerprint, files, ValidOcrSidecarCount: 0);

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Full);
    }

    [Fact]
    public void Decide_ReturnsFull_ForASingleFileContainer_WhenTheOutputIsMissing()
    {
        EncodingProfile profile = MakeMkvProfile();

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: true,
                BitmapSubtitleStreamCount: 0,
                ExistingOutputSnapshot.Empty
            )
        );

        decision.Action.Should().Be(ReconciliationAction.Full);
    }

    [Fact]
    public void Decide_ReturnsPartialOcrOnly_ForASingleFileContainer_WhenTheFileIsValidButOcrIsMissing()
    {
        EncodingProfile profile = MakeMkvProfile();
        ExistingOutputSnapshot existing = new(
            null,
            [new ExistingOutputEntry("Movie Title.NoMercy.mkv", 900_000_000)],
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(profile, IsSingleFileOutput: true, BitmapSubtitleStreamCount: 1, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Partial);
        decision.MissingKinds.Should().BeEmpty();
        decision.NeedsSubtitleOcr.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // ── Chapters vs a source that has none ───────────────────────────────────
    // FinalizeStage writes chapters.vtt only when the source has chapters, so a
    // source with none can never satisfy a demand for it. Asking anyway left the
    // media permanently incomplete: every dispatch flagged Chapters missing,
    // decomposition had no Chapters task to offer, and the job fell back to a
    // full re-encode — of a file that was already finished.

    [Fact]
    public void Decide_DoesNotAskForChapters_WhenTheSourceHasNone()
    {
        EncodingProfile profile = MakeHlsProfile();
        ExistingOutputSnapshot existing = new(
            ProfileFingerprint.Compute(profile),
            FilesWithoutChapters(),
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: false,
                BitmapSubtitleStreamCount: 0,
                existing,
                SourceChapterCount: 0
            )
        );

        decision.MissingKinds.Should().NotContain(EncodeTaskKind.Chapters);
        decision
            .Action.Should()
            .Be(
                ReconciliationAction.Skip,
                "everything the source can produce is present, so there is nothing to do"
            );
    }

    [Fact]
    public void Decide_StillAsksForChapters_WhenTheSourceHasThemAndTheVttIsMissing()
    {
        EncodingProfile profile = MakeHlsProfile();
        ExistingOutputSnapshot existing = new(
            ProfileFingerprint.Compute(profile),
            FilesWithoutChapters(),
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(
                profile,
                IsSingleFileOutput: false,
                BitmapSubtitleStreamCount: 0,
                existing,
                SourceChapterCount: 6
            )
        );

        decision
            .MissingKinds.Should()
            .Contain(
                EncodeTaskKind.Chapters,
                "the source has chapters, so a missing chapters.vtt is a real gap"
            );
    }

    // Two presets on one file share the same ExistingOutputSnapshot; a bare
    // "video_" prefix match satisfied either preset from the other's files.

    [Fact]
    public void Decide_ReturnsFull_WhenOnlyASiblingPresetsVideoSurvivesOnTheSharedRoot()
    {
        EncodingProfile preset1080p = MakeLibraryProfile("1080p HEVC 10-bit", 1920, 1080);

        // Only the 4K sibling preset's own rung, audio, and playlist are left
        // on disk — exactly Stoney's report: the 1080p rendition was deleted
        // by hand, but the shared root still carries the 4K preset's output.
        List<ExistingOutputEntry> sharedRootFiles =
        [
            new("web-1080p_master.m3u8", 500),
            new("video_3840x2160/video_3840x2160.m3u8", 300),
            new("audio_eng_aac/audio_eng_aac.m3u8", 200),
        ];
        ExistingOutputSnapshot existing = new(
            ProfileFingerprint.Compute(preset1080p),
            sharedRootFiles,
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(preset1080p, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision
            .Action.Should()
            .NotBe(
                ReconciliationAction.Skip,
                "the 1080p preset's own rung is missing — the 4K sibling's leftovers must not satisfy it"
            );
        decision.MissingKinds.Should().Contain(EncodeTaskKind.Video);
    }

    [Fact]
    public void Decide_ReturnsSkip_ForTheSiblingPreset_WhenItsOwnOutputReallyIsComplete_RegressionGuard()
    {
        // Counterpart to the test above, on the same shared-root snapshot: the
        // 4K preset's own rung really is present, so its reconciliation must
        // still Skip. The fix must narrow the match, not break it.
        EncodingProfile preset4k = MakeLibraryProfile("4K HDR HEVC 10-bit", 3840, 2160);

        List<ExistingOutputEntry> sharedRootFiles =
        [
            new("web-1080p_master.m3u8", 500),
            new("video_3840x2160/video_3840x2160.m3u8", 300),
            new("audio_eng_aac/audio_eng_aac.m3u8", 200),
        ];
        ExistingOutputSnapshot existing = new(
            ProfileFingerprint.Compute(preset4k),
            sharedRootFiles,
            ValidOcrSidecarCount: 0
        );

        ReconciliationDecision decision = _reconciler.Decide(
            new(preset4k, IsSingleFileOutput: false, BitmapSubtitleStreamCount: 0, existing)
        );

        decision.Action.Should().Be(ReconciliationAction.Skip);
        decision.MissingKinds.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static EncodingProfile MakeLibraryProfile(string name, int width, int height) =>
        new(
            Id: Ulid.NewUlid(),
            Name: name,
            Container: Container.HlsFmp4,
            Video: MakeVideoOutput() with
            {
                Width = width,
                Height = height,
            },
            Audio: [MakeAudioOutput()],
            Subtitles: []
        )
        {
            Thumbnails = null,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateMasterPlaylist = false,
                GenerateChapters = false,
            },
        };

    private static List<ExistingOutputEntry> FilesWithoutChapters() =>
        [.. AllPresentFiles().Where(f => f.RelativePath != "chapters.vtt")];

    // Names here mirror what the encoder actually writes. The sprite pair in
    // particular carries the `thumbs_{W}x{H}` shape at the current default tile
    // width — inventing a name that merely matches the reconciler would prove
    // nothing about a real output directory.
    private static List<ExistingOutputEntry> AllPresentFiles() =>
        [
            new("web-1080p_master.m3u8", 500),
            new("video_1920x1080_sdr/video_1920x1080_sdr.m3u8", 300),
            new("audio_eng_aac/audio_eng_aac.m3u8", 200),
            new("subtitles/eng.vtt", 150),
            new("chapters.vtt", 80),
            new("thumbs_320x180.webp", 298_000),
            new("thumbs_320x180.vtt", 4_000),
        ];

    private static EncodingProfile MakeHlsProfile() =>
        new(
            Id: Ulid.NewUlid(),
            Name: "web-1080p",
            Container: Container.HlsFmp4,
            Video: MakeVideoOutput(),
            Audio: [MakeAudioOutput()],
            Subtitles: [MakeSubtitleOutput()]
        );

    private static EncodingProfile MakeMkvProfile() =>
        new(
            Id: Ulid.NewUlid(),
            Name: "archive-mkv",
            Container: Container.Mkv,
            Video: MakeVideoOutput(),
            Audio: [MakeAudioOutput()],
            Subtitles: []
        );

    private static VideoOutput MakeVideoOutput() =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: VideoCodecType.H264,
            Width: 1920,
            Height: 1080,
            RateControl: NoMercy.Encoder.Profiles.RateControlMode.Crf,
            Crf: 23,
            BitrateKbps: 4000,
            MaxBitrateKbps: null,
            BufferSizeKbps: null,
            Preset: "medium",
            CodecProfile: CodecProfile.High,
            Level: null,
            Tune: null,
            BitDepth: 8,
            PixelFormat: null,
            KeyframeIntervalSeconds: 4,
            ConvertHdrToSdr: false,
            SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
            PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
        );

    private static AudioOutput MakeAudioOutput() =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: AudioCodecType.Aac,
            BitrateKbps: 128,
            Channels: 2,
            SampleRateHz: 48000,
            AllowedLanguages: ["eng"],
            DefaultLanguage: "eng",
            Loudness: null,
            Downmix: null,
            SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
            PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
        );

    private static SubtitleOutput MakeSubtitleOutput() =>
        new(
            Policy: SubtitlePolicy.Extract,
            Codec: SubtitleCodecType.WebVtt,
            AllowedLanguages: ["eng"],
            IncludeForced: false,
            OcrLanguage: null,
            PlaylistNameTemplate: "subtitles/:filename:.:language:.:variant:"
        );
}
