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
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Subtitles;
using NoMercy.Storage;

namespace NoMercy.Encoder.Reconciliation;

/// <summary>
/// Default <see cref="IEncodeReconciler"/>. Stateless — every dependency it
/// needs is either a constructor-free pure computation
/// (<see cref="ProfileFingerprint"/>) or passed in as a method parameter, so
/// the decision path stays trivially unit-testable without a filesystem, a
/// database, or a mock <see cref="IStorage"/>.
/// </summary>
public class EncodeReconciler : IEncodeReconciler
{
    /// <summary>Text sidecar formats the OCR pass can emit.</summary>
    private static readonly string[] TextSidecarExtensions = [".vtt", ".srt"];

    public ReconciliationDecision Decide(ReconciliationInput input)
    {
        if (input.Force)
            return new(
                ReconciliationAction.Full,
                [],
                input.BitmapSubtitleStreamCount > 0,
                "Force flag set on the job — reconciliation skipped, full re-encode."
            );

        EncodingProfile profile = input.Profile;
        ExistingOutputSnapshot existing = input.Existing;

        bool desiredOcr = input.BitmapSubtitleStreamCount > 0;
        bool hasValidOcr = existing.ValidOcrSidecarCount >= input.BitmapSubtitleStreamCount;
        bool needsOcr = desiredOcr && !hasValidOcr;

        if (input.IsSingleFileOutput)
            return DecideSingleFile(profile, existing, needsOcr);

        bool desiredVideo = profile.Video is not null || profile.Ladder?.Rungs is { Length: > 0 };
        bool desiredAudio = profile.Audio.Length > 0;
        bool desiredSubtitle = profile.Subtitles.Length > 0;
        // Mirror ThumbnailPlanBuilder: an explicit Thumbnails config wins, but
        // with none set it still builds a sprite whenever GenerateSpriteVtt is
        // on — which it is by default. Reading only profile.Thumbnails would
        // treat every preset that relies on that default as wanting no
        // thumbnails, so a missing sprite would never be topped up.
        bool desiredThumbnails =
            desiredVideo
            && (
                profile.Thumbnails is not null
                || (profile.HlsDerivatives ?? new HlsDerivatives()).GenerateSpriteVtt
            );
        // Only expect chapters.vtt from a source that has chapters to write —
        // FinalizeStage applies the same condition. Asking for it from a source
        // with none can never be satisfied, so the media reads as incomplete on
        // every pass and re-encodes in full, forever.
        bool desiredChapters =
            (profile.HlsDerivatives?.GenerateChapters ?? true) && input.SourceChapterCount > 0;
        bool desiredMasterPlaylist = profile.HlsDerivatives?.GenerateMasterPlaylist ?? true;

        // A missing or empty master playlist means the previous finalize
        // pass never completed — nothing downstream of it can be trusted
        // as a reliable "top up only the gap" case, so fall back to a full
        // re-encode rather than guessing which rungs are actually intact.
        if (desiredMasterPlaylist && !HasValidRootPlaylist(existing.BundleFiles))
            return new(
                ReconciliationAction.Full,
                [],
                needsOcr,
                "Master playlist missing or invalid — encode output is structurally incomplete."
            );

        List<EncodeTaskKind> missingKinds = [];
        if (desiredVideo && !HasValidUnder(existing.BundleFiles, "video_"))
            missingKinds.Add(EncodeTaskKind.Video);
        if (desiredAudio && !HasValidUnder(existing.BundleFiles, "audio_"))
            missingKinds.Add(EncodeTaskKind.Audio);
        if (desiredSubtitle && !HasValidDeclaredSubtitle(existing.BundleFiles))
            missingKinds.Add(EncodeTaskKind.Subtitle);
        // The sprite lands as `thumbs_{W}x{H}.webp` beside its matching `.vtt`;
        // the word "sprite" never appears on disk, so matching that would report
        // thumbnails missing on every re-dispatch. Present is not the whole test
        // either — a sheet rendered at a narrower tile than the profile now asks
        // for is a gap, and it costs one sprite pass to close rather than a
        // re-encode.
        if (
            desiredThumbnails && !HasAdequateSprite(existing.BundleFiles, DesiredTileWidth(profile))
        )
            missingKinds.Add(EncodeTaskKind.Thumbnails);
        if (desiredChapters && !HasValidNamed(existing.BundleFiles, "chapters.vtt"))
            missingKinds.Add(EncodeTaskKind.Chapters);

        return Finalize(profile, existing.ProfileFingerprint, missingKinds, needsOcr);
    }

    public async Task<ExistingOutputSnapshot> InspectAsync(
        string mediaRootPath,
        string presetId,
        IStorage destinationStorage,
        CancellationToken ct
    )
    {
        string trimmedRoot = mediaRootPath.TrimEnd('/');

        // Nothing on disk yet for this media (first-ever encode): an empty
        // snapshot is the right read. Asked of the storage, not guessed from
        // an exception: every backend answers ExistsAsync the same way, while
        // a listing can fail on output that is there. Read as empty, that
        // failure re-encoded the whole title (#359), so it now fails the
        // job, which is retried, and the output is left alone.
        if (!await destinationStorage.ExistsAsync(trimmedRoot, ct))
            return ExistingOutputSnapshot.Empty;

        List<ExistingOutputEntry> files = [];
        try
        {
            string dirPrefix = trimmedRoot + "/";
            foreach (
                StorageEntry entry in destinationStorage.List(trimmedRoot, "*", recursive: true)
            )
            {
                if (entry.IsDirectory)
                    continue;

                string rel = entry.Path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)
                    ? entry.Path[dirPrefix.Length..]
                    : entry.Path;
                files.Add(new(rel, entry.SizeBytes));
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Removed between the check and the listing: still nothing there.
            return ExistingOutputSnapshot.Empty;
        }

        int ocrCount = CountOcredBitmapSidecars(files);

        string? fingerprint = await TryReadBlueprintFingerprintAsync(
            trimmedRoot,
            presetId,
            files,
            destinationStorage,
            ct
        );

        return new(fingerprint, files, ocrCount);
    }

    /// <summary>
    /// Reads the fingerprint a finalized encode stamped into this preset's
    /// entry in the media item's <c>.nomercy.json</c> blueprint. Anything
    /// encoded before that stamp existed (or before the blueprint shipped)
    /// carries none, so a missing file, a missing preset entry, or an
    /// unreadable one is never an error — it is the expected case for every
    /// pre-existing library and falls through to the real on-disk listing
    /// instead.
    /// </summary>
    private static async Task<string?> TryReadBlueprintFingerprintAsync(
        string trimmedRoot,
        string presetId,
        IReadOnlyCollection<ExistingOutputEntry> files,
        IStorage destinationStorage,
        CancellationToken ct
    )
    {
        ExistingOutputEntry? blueprintEntry = files.FirstOrDefault(f =>
            string.Equals(
                Path.GetFileName(f.RelativePath),
                MediaBlueprintWriter.FileName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (blueprintEntry is null || !blueprintEntry.IsValid)
            return null;

        try
        {
            byte[] bytes = await destinationStorage.ReadAsync(
                $"{trimmedRoot}/{blueprintEntry.RelativePath}",
                ct
            );
            MediaBlueprint? blueprint = JsonConvert.DeserializeObject<MediaBlueprint>(
                Encoding.UTF8.GetString(bytes)
            );
            return blueprint
                ?.Encodes.FirstOrDefault(e =>
                    string.Equals(e.PresetId, presetId, StringComparison.OrdinalIgnoreCase)
                )
                ?.ProfileFingerprint;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ReconciliationDecision DecideSingleFile(
        EncodingProfile profile,
        ExistingOutputSnapshot existing,
        bool needsOcr
    )
    {
        // The container itself, not a sidecar beside it: subtitles/ can be
        // populated (by extraction or by OCR) while the mp4/mkv this decision is
        // about is missing entirely.
        bool desiredAnyStream = profile.Video is not null || profile.Audio.Length > 0;
        bool hasValidOutput = existing.BundleFiles.Any(f =>
            f.IsValid
            && !f.RelativePath.StartsWith("subtitles/", StringComparison.OrdinalIgnoreCase)
        );

        if (desiredAnyStream && !hasValidOutput)
            return new(
                ReconciliationAction.Full,
                [],
                needsOcr,
                "Single-file output missing or invalid — no partial concept for this container."
            );

        return Finalize(profile, existing.ProfileFingerprint, [], needsOcr);
    }

    /// <summary>
    /// Shared tail once every per-kind presence check has run: compares the
    /// stored fingerprint (if any) against the profile's current one and
    /// picks Full / Partial / Skip. A missing stored fingerprint (every
    /// output produced before fingerprinting shipped) is deliberately NOT
    /// treated as "profile changed" — that would force a full re-encode of
    /// an operator's entire library the moment the server upgrades.
    /// </summary>
    private static ReconciliationDecision Finalize(
        EncodingProfile profile,
        string? storedFingerprint,
        List<EncodeTaskKind> missingKinds,
        bool needsOcr
    )
    {
        // A fingerprint an older scheme wrote cannot be compared against one
        // computed now — unreadable, not different. It falls in with "no
        // fingerprint on record" below, which is the same deliberate call: a
        // server upgrade must never re-encode a library it has no evidence
        // against.
        bool fingerprintKnown = ProfileFingerprint.IsComparable(storedFingerprint);
        if (fingerprintKnown)
        {
            string currentFingerprint = ProfileFingerprint.Compute(profile);
            if (!string.Equals(storedFingerprint, currentFingerprint, StringComparison.Ordinal))
                return new(
                    ReconciliationAction.Full,
                    [],
                    needsOcr,
                    "Encoding profile changed since this output was produced."
                );
        }

        if (missingKinds.Count == 0 && !needsOcr)
            return new(
                ReconciliationAction.Skip,
                [],
                false,
                fingerprintKnown
                    ? "All desired outputs present, valid, and match the current profile fingerprint."
                    : "All desired outputs present and valid; no fingerprint on record (legacy output) treated as same profile."
            );

        List<string> gaps = missingKinds.Select(kind => kind.ToString()).ToList();
        if (needsOcr)
            gaps.Add("subtitle OCR sidecar");

        return new(
            ReconciliationAction.Partial,
            missingKinds,
            needsOcr,
            $"Profile unchanged — re-running only: {string.Join(", ", gaps)}."
        );
    }

    /// <summary>
    /// Counts bitmap subtitle sidecars that already have their OCR result: a
    /// text sidecar carrying the same <c>{lang}.{type}</c>. An OCR sidecar is
    /// named as its bitmap track's sibling (see <c>OcrSidecarTarget</c>) — it
    /// carries no marker distinguishing it from a declared text subtitle, and
    /// must not, because that name is exactly what makes a player list it.
    /// Pairing is therefore the only honest way to ask "has OCR run for this
    /// track", and it is the same question the library scan's orphan check asks.
    /// </summary>
    internal static int CountOcredBitmapSidecars(IReadOnlyCollection<ExistingOutputEntry> files)
    {
        HashSet<string> textKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExistingOutputEntry file in files)
        {
            if (
                file.IsValid
                && TryReadSubtitleKey(file.RelativePath, TextSidecarExtensions, out string textKey)
            )
                textKeys.Add(textKey);
        }

        // A bitmap track is only counted as OCR'd when a text sidecar carries the
        // same {lang}.{type} — the same pairing the library scan uses to decide
        // whether a bitmap subtitle is orphaned, and it reads bitmap-ness from
        // SubtitleClassifier so both agree on what a bitmap sidecar is.
        return files.Count(f =>
            f.IsValid
            && TryReadBitmapSubtitleKey(f.RelativePath, out string bitmapKey)
            && textKeys.Contains(bitmapKey)
        );
    }

    private static bool TryReadBitmapSubtitleKey(string relativePath, out string key)
    {
        key = string.Empty;
        string extension = Path.GetExtension(Path.GetFileName(relativePath));

        return SubtitleClassifier.IsBitmapSidecarExtension(extension)
            && TryReadSubtitleKey(relativePath, [extension], out key);
    }

    /// <summary>
    /// Reads the <c>{lang}.{type}</c> key out of a <c>{name}.{lang}.{type}.{ext}</c>
    /// sidecar filename, when its extension is one of <paramref name="extensions"/>.
    /// Mirrors <c>FileManager.SubtitleFileRegex</c>.
    /// </summary>
    private static bool TryReadSubtitleKey(string relativePath, string[] extensions, out string key)
    {
        key = string.Empty;
        string fileName = Path.GetFileName(relativePath);
        string extension = Path.GetExtension(fileName);

        if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        string[] parts = Path.GetFileNameWithoutExtension(fileName).Split('.');
        if (parts.Length < 2)
            return false;

        key = $"{parts[^2]}|{parts[^1]}";
        return true;
    }

    private static bool HasValidUnder(
        IReadOnlyCollection<ExistingOutputEntry> files,
        string prefix
    ) =>
        files.Any(f =>
            f.IsValid && f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>Whether the subtitle pass produced anything at all. An OCR
    /// sidecar is indistinguishable from a declared one by name (deliberately —
    /// see <see cref="CountOcredBitmapSidecars"/>), and both mean the same thing
    /// here: the subtitle stage ran and left output.</summary>
    private static bool HasValidDeclaredSubtitle(IReadOnlyCollection<ExistingOutputEntry> files) =>
        files.Any(f =>
            f.IsValid && f.RelativePath.StartsWith("subtitles/", StringComparison.OrdinalIgnoreCase)
        );

    private static bool HasValidNamed(
        IReadOnlyCollection<ExistingOutputEntry> files,
        string exactFileName
    ) =>
        files.Any(f =>
            f.IsValid
            && string.Equals(
                Path.GetFileName(f.RelativePath),
                exactFileName,
                StringComparison.OrdinalIgnoreCase
            )
        );

    /// <summary>The tile width this profile wants, from an explicit Thumbnails
    /// config if it has one and otherwise from its HLS derivatives — the same
    /// order <c>ThumbnailPlanBuilder</c> resolves it in.</summary>
    private static int DesiredTileWidth(EncodingProfile profile) =>
        profile.Thumbnails?.Width
        ?? (profile.HlsDerivatives ?? new HlsDerivatives()).SpriteVttThumbnailWidth;

    private static bool HasAdequateSprite(
        IReadOnlyCollection<ExistingOutputEntry> files,
        int desiredWidth
    ) =>
        files.Any(f =>
            f.IsValid && SpriteSheet.ReadTileWidth(Path.GetFileName(f.RelativePath)) >= desiredWidth
        );

    /// <summary>The master playlist sits directly at the media root, never
    /// inside a per-rung/kind subfolder.</summary>
    private static bool HasValidRootPlaylist(IReadOnlyCollection<ExistingOutputEntry> files) =>
        files.Any(f =>
            f.IsValid
            && !f.RelativePath.Contains('/')
            && f.RelativePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        );
}
