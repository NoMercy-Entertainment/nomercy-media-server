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
using Newtonsoft.Json;
using NoMercy.Encoder.Bundle;
using NoMercy.Encoder.Reconciliation;
using NoMercy.Tests.Encoder.Bundle;

namespace NoMercy.Tests.Encoder.Reconciliation;

/// <summary>
/// Exercises <see cref="EncodeReconciler.InspectAsync"/> against the
/// in-memory <see cref="TestStorage"/> fake — the real on-disk gathering
/// half of reconciliation, as opposed to <see cref="EncodeReconcilerDecideTests"/>
/// which covers the pure decision.
/// </summary>
public class EncodeReconcilerInspectAsyncTests
{
    private readonly EncodeReconciler _reconciler = new();

    [Fact]
    public async Task InspectAsync_ReturnsAnEmptySnapshot_WhenTheMediaRootHasNothingOnDisk()
    {
        TestStorage storage = new();

        ExistingOutputSnapshot snapshot = await _reconciler.InspectAsync(
            "Show/Show S01E01",
            "preset-id",
            storage,
            CancellationToken.None
        );

        snapshot.BundleFiles.Should().BeEmpty();
        snapshot.ValidOcrSidecarCount.Should().Be(0);
        snapshot.ProfileFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task InspectAsync_ListsFilesRelativeToTheMediaRoot_AndCountsOnlyValidOcrSidecars()
    {
        // An OCR sidecar is its bitmap track's sibling — same {lang}.{type} — so
        // it is counted by pairing, not by any marker in the name.
        TestStorage storage = new();
        const string root = "Show/Show S01E01";
        const string stem = "Show.S01E01.NoMercy";
        storage.Seed($"{root}/web-1080p_master.m3u8", [0x01, 0x02]);
        storage.Seed($"{root}/subtitles/{stem}.eng.full.mks", [0x01, 0x02]);
        storage.Seed($"{root}/subtitles/{stem}.eng.full.vtt", [0x01, 0x02, 0x03]);
        storage.Seed($"{root}/subtitles/{stem}.eng.sign.mks", [0x01, 0x02]);
        storage.Seed($"{root}/subtitles/{stem}.eng.sign.vtt", []); // truncated — must not count

        ExistingOutputSnapshot snapshot = await _reconciler.InspectAsync(
            root,
            "preset-id",
            storage,
            CancellationToken.None
        );

        snapshot
            .BundleFiles.Select(f => f.RelativePath)
            .Should()
            .BeEquivalentTo(
                "web-1080p_master.m3u8",
                $"subtitles/{stem}.eng.full.mks",
                $"subtitles/{stem}.eng.full.vtt",
                $"subtitles/{stem}.eng.sign.mks",
                $"subtitles/{stem}.eng.sign.vtt"
            );
        snapshot
            .ValidOcrSidecarCount.Should()
            .Be(
                1,
                "the sign track's sidecar is truncated (zero bytes) and must not count as valid"
            );
    }

    [Fact]
    public async Task InspectAsync_ReadsTheStoredProfileFingerprint_WhenTheBlueprintIsPresent()
    {
        TestStorage storage = new();
        const string root = "Show/Show S01E01";
        MediaBlueprint blueprint = MakeBlueprint(
            MakeEncode(presetId: "preset-id", profileFingerprint: "deadbeef")
        );
        storage.Seed(
            $"{root}/{MediaBlueprintWriter.FileName}",
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(blueprint))
        );

        ExistingOutputSnapshot snapshot = await _reconciler.InspectAsync(
            root,
            "preset-id",
            storage,
            CancellationToken.None
        );

        snapshot.ProfileFingerprint.Should().Be("deadbeef");
    }

    [Fact]
    public async Task InspectAsync_ReturnsNullFingerprint_WhenTheBlueprintHasNoMatchingPresetEntry()
    {
        TestStorage storage = new();
        const string root = "Show/Show S01E01";
        // Blueprint carries a sibling preset's entry only — the one being
        // reconciled has never been encoded yet.
        MediaBlueprint blueprint = MakeBlueprint(
            MakeEncode(presetId: "other-preset-id", profileFingerprint: "cafebabe")
        );
        storage.Seed(
            $"{root}/{MediaBlueprintWriter.FileName}",
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(blueprint))
        );

        ExistingOutputSnapshot snapshot = await _reconciler.InspectAsync(
            root,
            "preset-id",
            storage,
            CancellationToken.None
        );

        snapshot.ProfileFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task InspectAsync_ReturnsNullFingerprint_WhenTheBlueprintHasNoneRecorded_LegacyOutput()
    {
        TestStorage storage = new();
        const string root = "Show/Show S01E01";
        MediaBlueprint blueprint = MakeBlueprint(
            MakeEncode(presetId: "preset-id", profileFingerprint: null)
        );
        storage.Seed(
            $"{root}/{MediaBlueprintWriter.FileName}",
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(blueprint))
        );

        ExistingOutputSnapshot snapshot = await _reconciler.InspectAsync(
            root,
            "preset-id",
            storage,
            CancellationToken.None
        );

        snapshot.ProfileFingerprint.Should().BeNull();
    }

    // #359: a listing that fails on output that is there is not "nothing
    // encoded yet". Read as empty, Decide returned Full and the whole title
    // was encoded again. It fails instead, so the job is retried and the
    // output is left alone.
    [Fact]
    public async Task InspectAsync_Throws_WhenListingExistingOutputFails()
    {
        TestStorage storage = new() { ListFailure = new IOException("share went away") };
        const string root = "Show/Show S01E01";
        storage.Seed($"{root}/web-1080p_master.m3u8", [0x01, 0x02]);

        Func<Task> inspect = () =>
            _reconciler.InspectAsync(root, "preset-id", storage, CancellationToken.None);

        await inspect.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task InspectAsync_ReturnsAnEmptySnapshot_WhenTheFolderVanishesWhileListing()
    {
        TestStorage storage = new()
        {
            ListFailure = new DirectoryNotFoundException("removed between check and listing"),
        };
        const string root = "Show/Show S01E01";
        storage.Seed($"{root}/web-1080p_master.m3u8", [0x01, 0x02]);

        ExistingOutputSnapshot snapshot = await _reconciler.InspectAsync(
            root,
            "preset-id",
            storage,
            CancellationToken.None
        );

        snapshot.BundleFiles.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MediaBlueprint MakeBlueprint(BlueprintEncode encode) =>
        new(
            Version: 1,
            Identity: new(
                Type: "episode",
                TmdbId: 1,
                Show: new(TmdbId: 2, Title: "Show"),
                Season: 1,
                Episode: 1,
                Title: "Episode",
                Year: 2024
            ),
            Source: new(
                Path: "Download/Show/Show.S01E01.mkv",
                Filename: "Show.S01E01.mkv",
                Container: "matroska,webm",
                SizeBytes: 100,
                DurationSeconds: 60,
                Sha256: null,
                Ffprobe: null
            ),
            Encodes: [encode]
        );

    private static BlueprintEncode MakeEncode(string presetId, string? profileFingerprint) =>
        new(
            PresetSlug: "preset",
            PresetId: presetId,
            ProfileFingerprint: profileFingerprint,
            EncoderVersion: "3",
            TargetContainer: "matroska",
            OutputLocation: "Show/Show S01E01",
            CreatedAt: DateTime.UtcNow,
            CompletedAt: DateTime.UtcNow,
            Tracks: [],
            ReconstructionCommandTemplate: "ffmpeg -c copy \"reconstructed.mkv\"",
            LossyWarnings: []
        );
}
