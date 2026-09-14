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
namespace NoMercy.Api.Services;

/// <summary>
/// Transport-neutral outcome of a live-transcode operation. Lets
/// <see cref="LiveTranscodeService"/> own the domain decision (which kind of
/// failure, which message) while the controller stays responsible for mapping
/// each kind onto the matching HTTP response, preserving the existing wire
/// behaviour exactly.
/// </summary>
public enum LiveResultKind
{
    Ok,
    BadRequest,
    NotFound,
    Gone,
    ServiceUnavailable,
    InternalError,
    EncoderError,
}
