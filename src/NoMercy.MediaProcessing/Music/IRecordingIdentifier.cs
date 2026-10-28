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

using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

/// <summary>Names the MusicBrainz recordings an audio file's content matches.</summary>
public interface IRecordingIdentifier
{
    Task<IReadOnlyCollection<Guid>> IdentifyAsync(
        IStorageDriver driver,
        string path,
        CancellationToken cancellationToken
    );
}
