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

namespace NoMercy.Encoder.Subtitles;

/// <summary>
/// Default placeholder. The encoder registers this via <c>TryAddSingleton</c> so
/// <see cref="TesseractModelManager"/> (and any consumer that merely resolves it) always
/// resolves in an encoder-only DI container — mirroring
/// <see cref="NoOpOpenSubtitlesProvider"/>'s registration pattern. The Service host
/// registers the real signature/hash-verifying <c>TesseractModelDownloader</c> (in
/// NoMercy.Setup) after the encoder's own registration, replacing this via standard DI
/// last-wins.
/// </summary>
/// <remarks>
/// Unlike <see cref="NoOpOpenSubtitlesProvider"/> (whose no-op result — an empty search —
/// is a benign, valid outcome), an unverified model has no benign placeholder: writing
/// arbitrary bytes to disk as a "downloaded" language model would be a real correctness
/// and security regression if this default were ever actually invoked outside a test. It
/// throws instead of returning fabricated data.
/// </remarks>
public class NoOpTesseractModelDownloader : ITesseractModelDownloader
{
    public Task<IReadOnlyList<string>> GetAvailableLanguagesAsync(CancellationToken ct) =>
        throw new NotSupportedException(
            "No ITesseractModelDownloader is configured for this host."
        );

    public Task<Stream> DownloadVerifiedAsync(string language, CancellationToken ct) =>
        throw new NotSupportedException(
            "No ITesseractModelDownloader is configured for this host — the signed "
                + "nomercy-tesseract release downloader is registered by NoMercy.Service, "
                + "not by NoMercy.Encoder alone."
        );
}
