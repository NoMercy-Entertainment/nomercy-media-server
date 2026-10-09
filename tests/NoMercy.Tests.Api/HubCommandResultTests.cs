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
using Newtonsoft.Json.Linq;
using NoMercy.Api.Hubs;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Contract")]
public class HubCommandResultTests
{
    [Fact]
    public void WakeResult_KeepsLegacyStatusAndAddsReadableOutcome()
    {
        JObject success = JObject.Parse(
            JsonConvert.SerializeObject(HubCommandResult.FromWakeStatus("wake_sent"))
        );
        JObject failure = JObject.Parse(
            JsonConvert.SerializeObject(HubCommandResult.FromWakeStatus("not_owned"))
        );

        Assert.Equal("wake_sent", (string?)success["status"]);
        Assert.True((bool?)success["ok"]);
        Assert.Equal("not_owned", (string?)failure["status"]);
        Assert.False((bool?)failure["ok"]);
        Assert.Equal("not_found", (string?)failure["error_code"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)failure["message"]));
    }
}
