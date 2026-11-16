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

namespace NoMercy.Encoder.Commands;

/// <param name="ResumeMarkerPath">The marker that allowed a resumed run; a failed run deletes it.</param>
public record FfmpegCommand(
    string Executable,
    string[] Arguments,
    string? WorkingDirectory,
    string? ResumeMarkerPath = null
);
