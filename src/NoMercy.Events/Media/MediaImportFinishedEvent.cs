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

namespace NoMercy.Events.Media;

/// <summary>
/// Published exactly once by each import job a library scan dispatched: when
/// it succeeds, or when it fails for the last time. A job that is retried
/// publishes nothing for the attempts that are followed by another one.
/// </summary>
public sealed class MediaImportFinishedEvent : EventBase
{
    public override string Source => "MediaProcessor";

    public required Ulid LibraryId { get; init; }

    /// <summary>Titles this import added to the library (0 when it was already there).</summary>
    public required int Added { get; init; }

    /// <summary>1 when the import ended in failure, otherwise 0.</summary>
    public required int Failed { get; init; }
}
