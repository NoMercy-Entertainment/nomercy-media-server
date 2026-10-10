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

using System.Text.Json;
using FluentAssertions;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginSignedEnvelopeTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"unsigned\"")]
    [InlineData("{\"payload\":true,\"signature\":1}")]
    [InlineData("{\"payload\":true,\"signature\":null}")]
    [InlineData("{\"payload\":true,\"signature\":[]}")]
    [InlineData("{\"payload\":true,\"signature\":{\"alg\":1,\"kid\":\"k\",\"value\":\"v\"}}")]
    [InlineData(
        "{\"payload\":true,\"signature\":{\"alg\":\"ed25519\",\"kid\":{},\"value\":\"v\"}}"
    )]
    [InlineData(
        "{\"payload\":true,\"signature\":{\"alg\":\"ed25519\",\"kid\":\"k\",\"value\":[]}}"
    )]
    public void Malformed_signature_fields_are_rejected_without_throwing(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        PluginTrustedKeys trustedKeys = new(new Dictionary<string, string> { ["k"] = "key" });

        PluginSignedEnvelope
            .Verifies(document.RootElement, trustedKeys, "payload")
            .Should()
            .BeFalse();
    }
}
