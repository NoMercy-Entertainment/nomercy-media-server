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

namespace NoMercy.Events.Library;

/// <summary>
/// Published once per library scan, after the folder loop, with the number of
/// import jobs the queue actually accepted (TMDB misses and payloads already
/// queued are not counted). Zero is published too, so a listener never waits on
/// a scan that queued nothing. Jobs carry no scan id: the queue drops a payload
/// it already holds, so a listener follows a scan by <see cref="LibraryId"/>.
/// </summary>
public sealed class LibraryImportsQueuedEvent : EventBase
{
    public override string Source => "LibraryScanner";

    public required Ulid LibraryId { get; init; }
    public required string LibraryName { get; init; }
    public required int Count { get; init; }
}
