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

using Newtonsoft.Json;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Extension methods for creating ComponentEnvelopes fluently.
/// </summary>
public static class ComponentEnvelopeExtensions
{
    public static ComponentEnvelope WithId(this ComponentEnvelope envelope, Ulid id)
    {
        envelope.Id = id;
        return envelope;
    }

    public static ComponentEnvelope WithUpdate(this ComponentEnvelope envelope, UpdateDto? update)
    {
        envelope.Update = update;
        return envelope;
    }

    public static ComponentEnvelope WithReplacing(this ComponentEnvelope envelope, Ulid replacingId)
    {
        envelope.Replacing = replacingId;
        return envelope;
    }
}
