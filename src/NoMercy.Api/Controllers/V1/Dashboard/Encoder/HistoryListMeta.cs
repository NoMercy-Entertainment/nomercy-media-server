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
using Newtonsoft.Json;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

public record HistoryListMeta(
    [property: JsonProperty("total")] int Total,
    [property: JsonProperty("page_size")] int PageSize,
    [property: JsonProperty("page_index")] int PageIndex,
    [property: JsonProperty("total_pages")] int TotalPages
);
