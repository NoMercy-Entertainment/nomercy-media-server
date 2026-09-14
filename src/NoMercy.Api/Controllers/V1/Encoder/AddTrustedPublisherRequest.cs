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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Api.Controllers.V1.Encoder;

public record AddTrustedPublisherRequest(
    [property: JsonProperty("label")] string Label,
    [property: JsonProperty("public_key_base64")] string PublicKeyBase64
);
