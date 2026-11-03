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

using NoMercy.Encoder.Metadata;
using NoMercy.Encoder.Naming;

namespace NoMercy.Tests.Encoder.Metadata;

public class MetadataInjectorTests
{
    private readonly MetadataInjector _injector = new();

    // ------------------------------------------------------------------
    // Helper factories
    // ------------------------------------------------------------------

    private static MetadataInjectionContext MovieCtx(
        string title = "Fight Club",
        int? year = 1999,
        string? description = null,
        IReadOnlyList<TrackMetadata>? tracks = null,
        IReadOnlyList<string>? attachments = null
    ) =>
        new(
            Media: new MovieMediaRef(
                Type: MediaType.Movie,
                Id: 550,
                Title: title,
                Year: year,
                Description: description
            ),
            Tracks: tracks ?? [],
            AttachmentPaths: attachments ?? []
        );

    private static MetadataInjectionContext EpisodeCtx(
        string showTitle = "Breaking Bad",
        string episodeTitle = "Pilot",
        int seasonNumber = 1,
        int episodeNumber = 1,
        string? description = null,
        IReadOnlyList<TrackMetadata>? tracks = null
    ) =>
        new(
            Media: new EpisodeMediaRef(
                Type: MediaType.Episode,
                Id: 62085,
                Title: episodeTitle,
                Year: 2008,
                ShowTitle: showTitle,
                SeasonNumber: seasonNumber,
                EpisodeNumber: episodeNumber,
                Description: description
            ),
            Tracks: tracks ?? [],
            AttachmentPaths: []
        );

    // ------------------------------------------------------------------
    // F1-1: Movie → global -metadata flags
    // ------------------------------------------------------------------

    [Fact]
    public void BuildArgs_Movie_EmitsTitleAndYear()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx());

        int titleIdx = IndexOf(args, "-metadata", "title=Fight Club");
        titleIdx.Should().BeGreaterThanOrEqualTo(0, "expected -metadata title=Fight Club pair");

        int yearIdx = IndexOf(args, "-metadata", "year=1999");
        yearIdx.Should().BeGreaterThanOrEqualTo(0, "expected -metadata year=1999 pair");
    }

    [Fact]
    public void BuildArgs_Movie_NullYear_OmitsYearFlag()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(year: null));

        bool hasYear = args.SkipWhile(a => a != "-metadata")
            .Skip(1)
            .Take(1)
            .Any(v => v.StartsWith("year="));
        // Scan all -metadata pairs
        bool found = false;
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-metadata" && args[i + 1].StartsWith("year="))
            {
                found = true;
                break;
            }
        }
        found.Should().BeFalse("year=… must not appear when Year is null");
    }

    [Fact]
    public void BuildArgs_Movie_WithDescription_EmitsDescriptionFlag()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(
            MovieCtx(description: "An insomniac office worker.")
        );

        int descIdx = IndexOf(args, "-metadata", "description=An insomniac office worker.");
        descIdx.Should().BeGreaterThanOrEqualTo(0, "expected -metadata description=… pair");
    }

    [Fact]
    public void BuildArgs_Movie_NullDescription_OmitsDescriptionFlag()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(description: null));

        bool found = false;
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-metadata" && args[i + 1].StartsWith("description="))
            {
                found = true;
                break;
            }
        }
        found.Should().BeFalse("description flag must be absent when Description is null");
    }

    // ------------------------------------------------------------------
    // F1-2: Episode → extra show/season/episode flags
    // ------------------------------------------------------------------

    [Fact]
    public void BuildArgs_Episode_EmitsShowSeasonEpisodeFlags()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(EpisodeCtx());

        IndexOf(args, "-metadata", "show=Breaking Bad")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected show= flag");
        IndexOf(args, "-metadata", "season_number=1")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected season_number= flag");
        IndexOf(args, "-metadata", "episode_id=1")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected episode_id= flag");
    }

    [Fact]
    public void BuildArgs_Episode_EmitsTitleFlag()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(EpisodeCtx(episodeTitle: "Pilot"));

        IndexOf(args, "-metadata", "title=Pilot")
            .Should()
            .BeGreaterThanOrEqualTo(0, "episode title flag expected");
    }

    // ------------------------------------------------------------------
    // F1-3: Per-stream language
    // ------------------------------------------------------------------

    [Fact]
    public void BuildArgs_AudioTrack_EmitsStreamLanguageFlag()
    {
        TrackMetadata track = new(
            OutputIndex: 0,
            Kind: "audio",
            Language: "eng",
            Title: null,
            IsDefault: false,
            IsForced: false
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-metadata:s:a:0", "language=eng")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected -metadata:s:a:0 language=eng");
    }

    [Fact]
    public void BuildArgs_SubtitleTrack_UsesSubtitleStreamSpec()
    {
        TrackMetadata track = new(
            OutputIndex: 0,
            Kind: "subtitle",
            Language: "fra",
            Title: null,
            IsDefault: false,
            IsForced: false
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-metadata:s:s:0", "language=fra")
            .Should()
            .BeGreaterThanOrEqualTo(0, "subtitle stream spec must use :s:");
    }

    [Fact]
    public void BuildArgs_VideoTrack_UsesVideoStreamSpec()
    {
        TrackMetadata track = new(
            OutputIndex: 0,
            Kind: "video",
            Language: "eng",
            Title: null,
            IsDefault: false,
            IsForced: false
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-metadata:s:v:0", "language=eng")
            .Should()
            .BeGreaterThanOrEqualTo(0, "video stream spec must use :v:");
    }

    [Fact]
    public void BuildArgs_MultipleAudioTracks_EmitsIndexedSpecs()
    {
        TrackMetadata[] tracks =
        [
            new(0, "audio", "eng", null, true, false),
            new(1, "audio", "fra", null, false, false),
        ];
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: tracks));

        IndexOf(args, "-metadata:s:a:0", "language=eng").Should().BeGreaterThanOrEqualTo(0);
        IndexOf(args, "-metadata:s:a:1", "language=fra").Should().BeGreaterThanOrEqualTo(0);
    }

    // ------------------------------------------------------------------
    // F1-4: Disposition flags
    // ------------------------------------------------------------------

    [Fact]
    public void BuildArgs_DefaultAndForcedTrack_EmitsDispositionFlag()
    {
        TrackMetadata track = new(
            OutputIndex: 0,
            Kind: "audio",
            Language: "eng",
            Title: null,
            IsDefault: true,
            IsForced: true
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-disposition:a:0", "default+forced")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected -disposition:a:0 default+forced");
    }

    [Fact]
    public void BuildArgs_DefaultOnlyTrack_EmitsDefaultDisposition()
    {
        TrackMetadata track = new(
            OutputIndex: 0,
            Kind: "audio",
            Language: "eng",
            Title: null,
            IsDefault: true,
            IsForced: false
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-disposition:a:0", "default")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected -disposition:a:0 default");
    }

    [Fact]
    public void BuildArgs_ForcedOnlyTrack_EmitsForcedDisposition()
    {
        TrackMetadata track = new(
            OutputIndex: 0,
            Kind: "subtitle",
            Language: "eng",
            Title: null,
            IsDefault: false,
            IsForced: true
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-disposition:s:0", "forced")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected -disposition:s:0 forced");
    }

    [Fact]
    public void BuildArgs_NotDefaultNotForcedTrack_ClearsDisposition()
    {
        // Copy mode keeps the source disposition unless ffmpeg is told
        // otherwise, so an explicit "off" must be written out (issue #349).
        TrackMetadata track = new(
            OutputIndex: 1,
            Kind: "audio",
            Language: "fra",
            Title: null,
            IsDefault: false,
            IsForced: false
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-disposition:a:1", "0")
            .Should()
            .BeGreaterThanOrEqualTo(
                0,
                "a track the library marks not-default and not-forced must clear the source disposition with -disposition:a:1 0"
            );
    }

    [Fact]
    public void BuildArgs_NotDefaultNotForcedSubtitle_ClearsDisposition()
    {
        TrackMetadata track = new(
            OutputIndex: 0,
            Kind: "subtitle",
            Language: "eng",
            Title: null,
            IsDefault: false,
            IsForced: false
        );
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: [track]));

        IndexOf(args, "-disposition:s:0", "0")
            .Should()
            .BeGreaterThanOrEqualTo(0, "expected -disposition:s:0 0");
    }

    [Fact]
    public void BuildArgs_CopyModeMergeOfSourceDefaultAndDbNotDefault_ClearsDisposition()
    {
        // Issue #349 end to end: the source flags audio 0 as default, the
        // library says it is not. The merged track must clear the flag.
        SourceTrackMetadata source = new(
            OutputIndex: 0,
            Kind: "audio",
            Language: "eng",
            Title: null,
            Comment: null,
            IsDefault: true,
            IsForced: false
        );
        TrackMetadata db = new(
            OutputIndex: 0,
            Kind: "audio",
            Language: "eng",
            Title: null,
            IsDefault: false,
            IsForced: false
        );
        IReadOnlyList<TrackMetadata> merged = new MetadataMerger().Merge([source], [db]);

        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(tracks: merged));

        IndexOf(args, "-disposition:a:0", "0")
            .Should()
            .BeGreaterThanOrEqualTo(
                0,
                "the library's not-default must override the source default in copy mode"
            );
        args.Should().NotContain("default", "no stream may keep the source default flag");
    }

    // ------------------------------------------------------------------
    // F1-5: Cover-art attachment
    // ------------------------------------------------------------------

    [Fact]
    public void BuildArgs_WithAttachment_EmitsAttachAndMimetype()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(
            MovieCtx(attachments: ["/media/covers/cover.jpg"])
        );

        int attachIdx = Array.IndexOf(args.ToArray(), "-attach");
        attachIdx.Should().BeGreaterThanOrEqualTo(0, "expected -attach flag");
        args[attachIdx + 1].Should().Be("/media/covers/cover.jpg");

        // The mimetype metadata:s:t flag must appear after the -attach pair
        bool hasMime = false;
        for (int i = attachIdx + 2; i < args.Count - 1; i++)
        {
            if (args[i].StartsWith("-metadata:s:t") && args[i + 1] == "mimetype=image/jpeg")
            {
                hasMime = true;
                break;
            }
        }
        hasMime.Should().BeTrue("expected -metadata:s:t mimetype=image/jpeg after -attach");
    }

    [Fact]
    public void BuildArgs_WithPngAttachment_EmitsPngMimetype()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(
            MovieCtx(attachments: ["/media/covers/cover.png"])
        );

        bool hasMime = false;
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i].StartsWith("-metadata:s:t") && args[i + 1] == "mimetype=image/png")
            {
                hasMime = true;
                break;
            }
        }
        hasMime.Should().BeTrue("expected mimetype=image/png for .png attachment");
    }

    [Fact]
    public void BuildArgs_WithAttachment_EmitsFilenameTag()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(
            MovieCtx(attachments: ["/media/covers/cover.jpg"])
        );

        bool hasFilename = false;
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i].StartsWith("-metadata:s:t") && args[i + 1].StartsWith("filename="))
            {
                hasFilename = true;
                break;
            }
        }
        hasFilename.Should().BeTrue("expected -metadata:s:t:M filename= tag");
    }

    [Fact]
    public void BuildArgs_NoAttachments_NoAttachFlag()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx(attachments: []));

        args.Should().NotContain("-attach");
    }

    // ------------------------------------------------------------------
    // F1-6: Empty context produces only global flags (no crash)
    // ------------------------------------------------------------------

    [Fact]
    public void BuildArgs_EmptyTracksAndAttachments_ProducesNoStreamFlags()
    {
        IReadOnlyList<string> args = _injector.BuildArgs(MovieCtx());

        bool hasStreamMeta = args.Any(a =>
            a.StartsWith("-metadata:s:") || a.StartsWith("-disposition:")
        );
        hasStreamMeta.Should().BeFalse("no stream flags for empty track list");
    }

    // ------------------------------------------------------------------
    // Internal helper
    // ------------------------------------------------------------------

    /// <summary>Returns the index of <paramref name="flag"/> in <paramref name="args"/>
    /// where the next element equals <paramref name="value"/>, or -1.</summary>
    private static int IndexOf(IReadOnlyList<string> args, string flag, string value)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag && args[i + 1] == value)
                return i;
        }
        return -1;
    }
}
