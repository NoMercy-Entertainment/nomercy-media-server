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

public interface ITesseractModelManager
{
    Task<string> EnsureLanguageModelAsync(string language, CancellationToken ct);

    Task<IReadOnlyList<string>> GetAvailableLanguagesAsync(CancellationToken ct);

    IReadOnlyList<string> GetDownloadedLanguages();

    string ModelDirectory { get; }
}
