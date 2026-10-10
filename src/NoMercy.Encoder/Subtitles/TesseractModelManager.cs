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
using NoMercy.Storage;

namespace NoMercy.Encoder.Subtitles;

/// <summary>
/// Ensures Tesseract *.traineddata language models are available on disk, downloading
/// missing ones through <see cref="ITesseractModelDownloader"/> — the signed
/// NoMercy-Entertainment/nomercy-tesseract release, never an unverified raw fetch.
/// Idempotent — repeat calls for an existing language are a no-op.
/// </summary>
public class TesseractModelManager(
    EncoderOptions options,
    ITesseractModelDownloader downloader,
    IStorage storage,
    ILogger<TesseractModelManager> logger
) : ITesseractModelManager
{
    public string ModelDirectory =>
        options.TesseractModelsDirectory
        ?? throw new InvalidOperationException(
            "TesseractModelsDirectory is not configured on EncoderOptions."
        );

    public async Task<string> EnsureLanguageModelAsync(string language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language code must be non-empty", nameof(language));

        storage.CreateDirectory(ModelDirectory);

        string fileName = $"{language}.traineddata";
        string localPath = Path.Combine(ModelDirectory, fileName);

        if (storage.Exists(localPath))
        {
            logger.LogDebug("Tesseract model already present: {FileName}", fileName);
            return localPath;
        }

        logger.LogInformation(
            "Downloading verified Tesseract model {FileName} from the signed nomercy-tesseract release",
            fileName
        );

        // Stream to a temp file then rename — never leave a half-written model on disk
        // if the download is cancelled midway. The downloader itself has already verified
        // the manifest signature and the SHA-256 before handing back any bytes, so nothing
        // reaches disk here unless it passed both checks.
        string tempPath = $"{localPath}.tmp";
        try
        {
            await using Stream verified = await downloader.DownloadVerifiedAsync(language, ct);
            await using (Stream file = await storage.OpenWriteAsync(tempPath, overwrite: true, ct))
            {
                await verified.CopyToAsync(file, ct);
            }

            // Atomic-ish swap: delete any prior model then move the
            // freshly downloaded temp into its final place.
            storage.Delete(localPath);
            storage.Move(tempPath, localPath);
        }
        catch
        {
            storage.Delete(tempPath);
            throw;
        }

        logger.LogInformation("Tesseract model saved: {Path}", localPath);
        return localPath;
    }

    public Task<IReadOnlyList<string>> GetAvailableLanguagesAsync(CancellationToken ct) =>
        downloader.GetAvailableLanguagesAsync(ct);

    public IReadOnlyList<string> GetDownloadedLanguages()
    {
        if (!storage.Exists(ModelDirectory))
            return [];

        return
        [
            .. storage
                .List(ModelDirectory, "*.traineddata", recursive: false)
                .Where(e => !e.IsDirectory)
                .Select(e => Path.GetFileNameWithoutExtension(e.Path))
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!),
        ];
    }
}
