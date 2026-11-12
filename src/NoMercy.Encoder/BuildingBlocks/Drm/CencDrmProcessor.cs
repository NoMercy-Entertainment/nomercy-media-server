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

using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Storage;

namespace NoMercy.Encoder.BuildingBlocks.Drm;

/// <summary>
/// CENC raw-key packaging via shaka-packager.
///
/// Implements the <em>packaging</em> side of CENC DRM for DASH:
///   - Takes unencrypted mp4/webm segments already produced by ffmpeg.
///   - Invokes shaka-packager in raw-key mode (<c>--enable_raw_key_encryption</c>).
///   - Writes PSSH boxes for Widevine and PlayReady into the MPD.
///   - Returns the path to the generated MPD manifest.
///
/// What this does NOT do:
///   - License server wiring (Phase 11.2b).
///   - Client CDM / EME key-session negotiation (Phase 11.2c).
///   - Key generation — callers supply keys via <see cref="DrmConfig.CencKeys"/>.
///
/// shaka-packager documentation:
///   https://shaka-project.github.io/shaka-packager/html/documentation.html
/// </summary>
public class CencDrmProcessor(
    EncoderOptions options,
    IProcessRunner processRunner,
    ILogger<CencDrmProcessor> logger,
    IStorage storage
) : IDrmProcessor
{
    public DrmMethod Method => DrmMethod.Cenc;

    /// <summary>
    /// CENC does not write pre-encode artifacts (no key-info file for ffmpeg).
    /// Calling this on a <see cref="DrmMethod.Cenc"/> config returns a
    /// sentinel <see cref="DrmArtifact"/> with empty paths — the real work
    /// happens in <see cref="PackageAsync"/> after ffmpeg finishes.
    /// </summary>
    public Task<DrmArtifact> PrepareAsync(
        string outputDirectory,
        DrmConfig config,
        CancellationToken ct
    )
    {
        if (config.Method != DrmMethod.Cenc)
            throw new ArgumentException(
                $"This processor handles CENC only, got {config.Method}",
                nameof(config)
            );

        // CENC encryption happens post-encode via PackageAsync.
        // Return an empty sentinel so BuildStage's DRM wiring is a no-op.
        return Task.FromResult(
            new DrmArtifact(
                KeyInfoFilePath: string.Empty,
                KeyFilePath: string.Empty,
                KeyUri: config.KeyUri,
                Key: [],
                Iv: []
            )
        );
    }

    /// <summary>
    /// Runs shaka-packager over the unencrypted DASH segments produced by ffmpeg,
    /// applying raw-key CENC encryption in place.
    /// </summary>
    /// <param name="outputDirectory">
    /// Directory containing the unencrypted DASH segments and init segments.
    /// </param>
    /// <param name="streamDescriptors">
    /// One descriptor per stream. Each maps a local segment file to a shaka-packager
    /// stream spec (<c>in=…,stream=…,output=…,drm_label=…</c>).
    /// </param>
    /// <param name="config">
    /// DRM configuration. Must have <see cref="DrmMethod.Cenc"/> and at least one
    /// <see cref="DrmConfig.CencKeys"/> entry.
    /// </param>
    /// <param name="mpdOutputPath">Absolute path where the MPD manifest will be written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the generated MPD manifest.</returns>
    public async Task<string> PackageAsync(
        string outputDirectory,
        IReadOnlyList<CencStreamDescriptor> streamDescriptors,
        DrmConfig config,
        string mpdOutputPath,
        CancellationToken ct
    )
    {
        if (config.Method != DrmMethod.Cenc)
            throw new ArgumentException(
                $"PackageAsync requires DrmMethod.Cenc, got {config.Method}",
                nameof(config)
            );

        IReadOnlyList<CencKeyEntry> keys =
            config.CencKeys
            ?? throw new ArgumentException(
                "DrmConfig.CencKeys must be set for CENC packaging",
                nameof(config)
            );

        if (keys.Count == 0)
            throw new ArgumentException(
                "DrmConfig.CencKeys must contain at least one entry",
                nameof(config)
            );

        string packagerPath = options.GetShakaPackagerPath(storage);

        List<string> args = BuildPackagerArguments(streamDescriptors, keys, mpdOutputPath);

        logger.LogInformation(
            "Running shaka-packager for CENC packaging: {Args}",
            string.Join(" ", args.Select(RedactKey))
        );

        ProcessResult result = await processRunner
            .RunAsync(
                packagerPath,
                [.. args],
                workingDirectory: outputDirectory,
                cancellationToken: ct
            )
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"shaka-packager exited with code {result.ExitCode}. "
                    + "Check logs for details. "
                    + $"stderr: {result.StdErr}"
            );

        if (!storage.Exists(mpdOutputPath))
            throw new InvalidOperationException(
                $"shaka-packager completed but MPD not found at: {mpdOutputPath}"
            );

        logger.LogInformation("CENC packaging complete. MPD: {MpdPath}", mpdOutputPath);
        return mpdOutputPath;
    }

    /// <summary>
    /// Builds the shaka-packager argument list for raw-key CENC encryption.
    ///
    /// Example output for one SD video stream:
    /// <code>
    ///   packager
    ///     'in=video_init.mp4,stream=video,output=video_enc.mp4,drm_label=SD'
    ///     --enable_raw_key_encryption
    ///     --keys label=SD:key_id=&lt;hex&gt;:key=&lt;hex&gt;
    ///     --mpd_output manifest.mpd
    /// </code>
    /// </summary>
    internal static List<string> BuildPackagerArguments(
        IReadOnlyList<CencStreamDescriptor> streamDescriptors,
        IReadOnlyList<CencKeyEntry> keys,
        string mpdOutputPath
    )
    {
        List<string> args = [];

        // Stream descriptors — one positional arg per stream
        foreach (CencStreamDescriptor descriptor in streamDescriptors)
            args.Add(descriptor.ToPackagerSpec());

        args.Add("--enable_raw_key_encryption");

        // --keys label=LABEL:key_id=HEX:key=HEX[,label=LABEL:...]
        // shaka-packager accepts comma-separated entries or repeated --keys flags.
        // We use repeated flags for clarity.
        foreach (CencKeyEntry entry in keys)
        {
            args.Add("--keys");
            args.Add(
                $"label={entry.Label}:key_id={Convert.ToHexString(entry.KeyId).ToLowerInvariant()}:key={Convert.ToHexString(entry.Key).ToLowerInvariant()}"
            );
        }

        args.Add("--mpd_output");
        args.Add(mpdOutputPath);

        return args;
    }

    // The content key is a secret; the log keeps the label and key id only.
    internal static string RedactKey(string arg)
    {
        int keyAt = arg.IndexOf(":key=", StringComparison.Ordinal);
        return keyAt < 0 ? arg : string.Concat(arg.AsSpan(0, keyAt), ":key=<redacted>");
    }
}

/// <summary>
/// Describes a single stream to be passed to shaka-packager.
/// Maps the unencrypted input file to the encrypted output file and declares
/// the stream type and DRM label.
/// </summary>
public record CencStreamDescriptor(
    string InputPath,
    string StreamType,
    string OutputPath,
    string DrmLabel
)
{
    /// <summary>
    /// Produces a shaka-packager positional stream spec string.
    /// Forward slashes are used for cross-platform compatibility.
    /// </summary>
    public string ToPackagerSpec()
    {
        string input = InputPath.Replace('\\', '/');
        string output = OutputPath.Replace('\\', '/');
        return $"in={input},stream={StreamType},output={output},drm_label={DrmLabel}";
    }
}
