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

internal sealed record CorpusMismatch(
    SceneCorpusEntry Entry,
    CorpusMode Mode,
    string Field,
    string Ours,
    string Theirs
)
{
    public override string ToString() =>
        $"[{Mode}] {Entry.Name} | {Field}: ours '{Ours}', source '{Theirs}'";
}
