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
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Progress;
using NoMercy.Resources;
using NoMercy.Storage;

namespace NoMercy.Encoder.Subtitles;

/// <summary>
/// Produces a WebVTT or SRT subtitle file by running FFmpeg's <c>whisper</c>
/// filter (whisper.cpp) against a specific audio stream in the input.
/// The filter writes the subtitle output directly — no post-parsing needed.
/// </summary>
public class WhisperTranscriber(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    ILogger<WhisperTranscriber> logger
) : IWhisperTranscriber
{
    public async Task<SubtitleTrack> TranscribeAsync(
        string inputPath,
        int audioStreamIndex,
        string language,
        WhisperOptions? options_,
        IProgressObserver? progress,
        CancellationToken ct
    )
    {
        string modelPath =
            options_?.ModelPath
            ?? options.WhisperModelPath
            ?? throw new InvalidOperationException(
                "WhisperModelPath is not configured on EncoderOptions and no override supplied."
            );

        if (!storage.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Whisper model not found at {modelPath}. Configure EncoderOptions.WhisperModelPath.",
                modelPath
            );
        }

        string outputDirectory =
            Path.GetDirectoryName(inputPath)
            ?? throw new InvalidOperationException("Input path has no parent directory.");

        string outputName = $"{Path.GetFileNameWithoutExtension(inputPath)}.{language}.whisper";
        string format = "srt"; // whisper filter supports srt; we emit VTT by extension rename afterwards if requested
        string outputPath = Path.Combine(outputDirectory, $"{outputName}.{format}");

        // Ensure a stable run location for the whisper filter — it resolves
        // destination= relative to CWD.
        storage.CreateDirectory(outputDirectory);

        // Lease every path we hand to ffmpeg so future remote drivers can
        // stage them locally + clean up on dispose.
        await using LocalPathLease inputLease = storage.AcquireLocalPath(inputPath);
        await using LocalPathLease modelLease = storage.AcquireLocalPath(modelPath);
        await using LocalPathLease outputLease = storage.AcquireLocalPath(outputPath);

        // Select audio stream, apply whisper filter with model, language, queue,
        // destination, and format, then discard video output.
        int queue = 3;
        int translate = options_?.TranslateToEnglish == true ? 1 : 0;

        string whisperFilter =
            $"whisper=model={FilterGraphPathEscaper.Escape(modelLease.Path)}:language={language}"
            + $":queue={queue}:destination={FilterGraphPathEscaper.Escape(outputLease.Path)}:format={format}"
            + (translate == 1 ? ":translate=1" : "");

        string[] args =
        [
            "-hide_banner",
            // Ahead of -i: after it, the cap reaches the output and leaves the
            // decoder to take the box.
            "-threads",
            EncodeThreadBudget.AuxiliaryPass.ToString(),
            "-i",
            inputLease.Path,
            "-map",
            $"0:a:{audioStreamIndex}",
            "-vn",
            "-af",
            whisperFilter,
            "-f",
            "null",
            "-",
        ];

        progress?.OnStageStarted($"Whisper transcription ({language})");

        ProcessResult result = await processRunner.RunAsync(
            options.FfmpegPath,
            args,
            workingDirectory: outputDirectory,
            cancellationToken: ct
        );

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"whisper ffmpeg exited with code {result.ExitCode}"
            );
        }

        if (!storage.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"Whisper filter produced no output at {outputPath}."
            );
        }

        int cueCount = CountCuesIn(outputPath);

        progress?.OnStageCompleted($"Whisper transcription ({language})", result.Duration);

        logger.LogInformation(
            "Whisper transcription complete: {Cues} cues for {Language} → {Path}",
            cueCount,
            language,
            outputPath
        );

        return new(outputPath, language, SubtitleCodecType.Srt, cueCount);
    }

    private int CountCuesIn(string srtPath)
    {
        int count = 0;
        using Stream stream = storage.OpenRead(srtPath);
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("-->", StringComparison.Ordinal))
                count++;
        }
        return count;
    }
}
