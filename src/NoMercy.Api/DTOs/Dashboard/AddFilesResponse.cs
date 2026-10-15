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

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>Acknowledges queued imports while retaining the existing request-echo fields.</summary>
public record AddFilesResponse : AddFilesRequest
{
    public AddFilesResponse(AddFilesRequest request)
        : base(request) { }

    [JsonProperty("status")]
    public string Status => "ok";
}
