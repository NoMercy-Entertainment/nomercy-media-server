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

using System.Globalization;
using System.Text;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Progress;
using NoMercy.Resources;
using NoMercy.Storage;

namespace NoMercy.Encoder.PostProcess;

/// <inheritdoc />
// EncoderOptions is registered as a configured singleton, not through the options
// pattern. Asking for IOptions<EncoderOptions> resolves — with a default-constructed
// instance that carries no ffmpeg path, so every run threw before reaching ffmpeg.
public class SpriteSheetRefresher(
    IMediaAnalyzer analyzer,
    IFfmpegExecutor executor,
    EncoderOptions options
) : ISpriteSheetRefresher
{
    private static readonly string[] ProgressiveExtensions =
    [
        ".mp4",
        ".mkv",
        ".m4v",
        ".webm",
        ".mov",
        ".avi",
        ".ts",
    ];

    public async Task<string?> RefreshAsync(
        IStorage storage,
        string mediaFolder,
        int tileWidth,
        int intervalSeconds,
        Action<EncodingProgress>? onProgress = null,
        CancellationToken ct = default
    )
    {
        string? source = FindPlayableSource(storage, mediaFolder);
        if (source is null)
            return null;

        MediaInfo media = await analyzer.AnalyzeAsync(storage.GetFullPath(source), ct);
        if (media.VideoStreams.Count == 0)
            return null;

        int tileHeight = TileHeightFor(tileWidth, media.VideoStreams[0]);
        string sheetName = $"thumbs_{tileWidth}x{tileHeight}.webp";
        string vttName = $"thumbs_{tileWidth}x{tileHeight}.vtt";

        // Pin the grid and fill it exactly. Left to itself the muxer leaves the
        // tail of the last row untouched, which comes out green rather than
        // blank — see SpriteGrid.
        SpriteGrid grid = SpriteGrid.For(media.Duration, intervalSeconds);

        // The encoded rendition is already SDR, so no tone-map chain: this is the
        // one sprite path that can say that for certain, which is why it reads
        // the output instead of hunting down a source it may no longer have.
        // The pipe costs a line of stderr parsing per second and buys the only
        // numbers this run can produce, so it follows the caller: on when someone
        // is listening, off when nobody is.
        FfmpegCommand command = new FfmpegCommandBuilder()
            .WithGlobalOptions(
                new(
                    ProgressPipe: onProgress is not null,
                    Overwrite: true,
                    Threads: EncodeThreadBudget.AuxiliaryPass
                )
            )
            .AddInput(new(storage.GetFullPath(source)))
            .AddOutput(
                new(
                    FilePath: sheetName,
                    MapStreams: ["0:v:0"],
                    ExtraFlags: new()
                    {
                        ["-vf"] = ThumbnailFilterResolver.Resolve(
                            intervalSeconds,
                            tileWidth,
                            sourceIsHdr: false,
                            tonemapChain: null,
                            padToCells: grid.CellCount
                        ),
                        ["-frames:v"] = grid.CellCount.ToString(CultureInfo.InvariantCulture),
                        ["-f"] = "spritevtt",
                        ["-sprite_columns"] = grid.Columns.ToString(CultureInfo.InvariantCulture),
                        ["-vtt_filename"] = vttName,
                        // An output -threads reaches the muxer's encoder and
                        // nothing else: the h264 decoder took every core anyway,
                        // 10.6 of them measured. Only the global one above caps it.
                        ["-threads"] = EncodeThreadBudget.AuxiliaryPass.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        ["-filter_threads"] = EncodeThreadBudget.AuxiliaryPass.ToString(
                            CultureInfo.InvariantCulture
                        ),
                    }
                )
            )
            .Build(options.FfmpegPath, storage.GetFullPath(mediaFolder));

        ExecutionResult result = await executor.ExecuteAsync(
            command,
            media.Duration,
            onProgress,
            ct: ct
        );
        if (!result.Success)
            return null;

        await TrimPaddingCuesAsync(storage, mediaFolder, vttName, media.Duration, ct);
        RemoveSupersededSheets(storage, mediaFolder, keep: sheetName);
        return sheetName;
    }

    /// <summary>
    /// Removes the cues covering the padding tiles. Failure here is not worth
    /// discarding a good sheet over: the tiles it would have hidden sit past the
    /// end of the film, so only the TV strip would show them.
    /// </summary>
    private static async Task TrimPaddingCuesAsync(
        IStorage storage,
        string mediaFolder,
        string vttName,
        TimeSpan duration,
        CancellationToken ct
    )
    {
        string vttPath = storage.CombinePath(mediaFolder, vttName);

        try
        {
            if (!storage.Exists(vttPath))
                return;

            byte[] raw = await storage.ReadAsync(vttPath, ct);
            string trimmed = SpriteVttTrimmer.Trim(Encoding.UTF8.GetString(raw), duration);
            await storage.WriteAsync(vttPath, Encoding.UTF8.GetBytes(trimmed), ct);
        }
        catch (Exception)
        {
            // Nothing to recover: the sheet is already written and correct.
        }
    }

    /// <summary>
    /// Height that preserves the encoded picture's own shape, rounded to an even
    /// number because the encoders downstream of this reject odd dimensions.
    /// Mirrors <c>ThumbnailPlanBuilder</c> so a refreshed sheet is dimensioned
    /// exactly as a freshly encoded one would be.
    /// </summary>
    internal static int TileHeightFor(int tileWidth, VideoStreamInfo video) =>
        (int)(2 * Math.Round((double)tileWidth * video.Height / video.Width / 2));

    /// <summary>
    /// Something in the folder to sample frames from: a rendition playlist when
    /// the title was encoded to HLS, otherwise the progressive file itself.
    ///
    /// <para>The progressive fallback is not an edge case. Every title carrying
    /// a legacy <c>sprite.webp</c> predates the HLS layout and holds one flat
    /// <c>.mp4</c> with no <c>video_*</c> folder at all — exactly the population
    /// this job exists to upgrade. Looking only for renditions meant all 4218
    /// queued jobs gave up on "nothing playable to sample" and not one sheet was
    /// ever rebuilt.</para>
    /// </summary>
    private static string? FindPlayableSource(IStorage storage, string mediaFolder) =>
        FindRenditionPlaylist(storage, mediaFolder) ?? FindProgressiveFile(storage, mediaFolder);

    /// <summary>
    /// A rendition playlist to sample frames from. The master playlist sits at
    /// the folder root and names the renditions, but ffmpeg reading the master
    /// picks a variant by its own rules; addressing a rendition directly keeps
    /// the frames tied to a known picture size, which is what the tile height is
    /// derived from. Highest rendition wins — the sheet is downscaled from it
    /// either way, and scaling down from the best copy is the sharper result.
    /// </summary>
    private static string? FindRenditionPlaylist(IStorage storage, string mediaFolder)
    {
        List<string> renditions = [];

        foreach (StorageEntry entry in storage.List(mediaFolder, null, recursive: false))
        {
            if (!entry.IsDirectory)
                continue;

            string name = storage.GetName(entry.Path);
            if (!name.StartsWith("video_", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (StorageEntry inner in storage.List(entry.Path, null, recursive: false))
            {
                if (
                    !inner.IsDirectory
                    && storage
                        .GetName(inner.Path)
                        .EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                )
                    renditions.Add(
                        storage.CombinePath(mediaFolder, $"{name}/{storage.GetName(inner.Path)}")
                    );
            }
        }

        return renditions
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// The progressive video file a pre-HLS title was encoded to. Largest wins,
    /// because the folder can also hold a short extra beside the feature and the
    /// sheet must describe the feature.
    ///
    /// <para>Only the folder root is read: <c>original/</c> beside it holds the
    /// untouched source, which may be HDR and is not what any client plays.</para>
    /// </summary>
    private static string? FindProgressiveFile(IStorage storage, string mediaFolder)
    {
        StorageEntry? largest = null;

        foreach (StorageEntry entry in storage.List(mediaFolder, null, recursive: false))
        {
            if (entry.IsDirectory)
                continue;

            string extension = Path.GetExtension(storage.GetName(entry.Path));
            if (!ProgressiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                continue;

            if (largest is null || entry.SizeBytes > largest.SizeBytes)
                largest = entry;
        }

        return largest is null
            ? null
            : storage.CombinePath(mediaFolder, storage.GetName(largest.Path));
    }

    /// <summary>
    /// Drops every sheet and cue file except the one just written. A stale pair
    /// left beside the new one is not harmless: the scan registers whatever
    /// sprite it finds, so the old sheet can win and the upgrade goes unnoticed.
    /// </summary>
    private static void RemoveSupersededSheets(IStorage storage, string mediaFolder, string keep)
    {
        string keepStem = Path.GetFileNameWithoutExtension(keep);

        foreach (StorageEntry entry in storage.List(mediaFolder, null, recursive: false))
        {
            if (entry.IsDirectory)
                continue;

            string name = storage.GetName(entry.Path);
            string stem = Path.GetFileNameWithoutExtension(name);

            if (stem.Equals(keepStem, StringComparison.OrdinalIgnoreCase))
                continue;

            // The legacy pair states no tile size, so the name check below cannot
            // recognise it and it outlived every rebuild — the scan then had two
            // sheets to choose from and could still register the old one.
            bool legacy =
                SpriteSheet.IsLegacySheet(name)
                || name.Equals("previews.vtt", StringComparison.OrdinalIgnoreCase);

            if (!legacy && SpriteSheet.ReadTileWidth($"{stem}.webp") is null)
                continue;

            storage.Delete(entry.Path);
        }
    }
}
