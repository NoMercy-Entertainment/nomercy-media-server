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

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>A corpus name whose title differs from its source and has not been reviewed.</summary>
public sealed record TitleBaselineEntry(
    string Name,
    string Source,
    string Mode,
    string Ours,
    string Theirs,
    string Label
);
