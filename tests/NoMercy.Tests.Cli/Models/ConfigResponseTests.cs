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

using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Cli.Models;
using Xunit;

namespace NoMercy.Tests.Cli.Models;

/// <summary>
/// REQUIREMENT: <see cref="ConfigResponse"/> is the CLI's copy of the server's
/// <c>GET /manage/config</c> body (<c>ManagementConfigDto</c>). The two must
/// agree key for key: a key the CLI declares but the server never sends prints
/// as a permanent default, and a key the server sends but the CLI does not
/// declare is silently dropped. The JSON below is that body, written out with
/// a distinct non-default value per key.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConfigResponseTests
{
    private const string ServerConfigJson = """
        {
          "internal_port": 7626,
          "external_port": 7627,
          "server_name": "nomercy-test",
          "library_workers": 1,
          "import_workers": 2,
          "extras_workers": 3,
          "encoder_workers": 4,
          "cron_workers": 5,
          "image_workers": 6,
          "file_workers": 7,
          "music_workers": 8,
          "swagger": true
        }
        """;

    private static IEnumerable<PropertyInfo> SerializedProperties =>
        typeof(ConfigResponse)
            .GetProperties()
            .Where(property => property.GetCustomAttribute<JsonPropertyAttribute>() is not null);

    [Fact]
    public void DeclaresExactlyTheKeysTheServerSends()
    {
        List<string> declaredKeys = SerializedProperties
            .Select(property => property.GetCustomAttribute<JsonPropertyAttribute>()!.PropertyName!)
            .ToList();
        List<string> serverKeys = JObject
            .Parse(ServerConfigJson)
            .Properties()
            .Select(property => property.Name)
            .ToList();

        declaredKeys.Except(serverKeys).Should().BeEmpty("the server never sends these keys");
        serverKeys.Except(declaredKeys).Should().BeEmpty("the CLI silently drops these keys");
    }

    [Fact]
    public void Deserialize_ServerConfigJson_FillsEveryWorkerCount()
    {
        ConfigResponse config = JsonConvert.DeserializeObject<ConfigResponse>(ServerConfigJson)!;

        List<string> unfilledWorkerCounts = SerializedProperties
            .Where(property => property.PropertyType == typeof(int))
            .Where(property => property.Name.EndsWith("Workers", StringComparison.Ordinal))
            .Where(property => (int)property.GetValue(config)! == 0)
            .Select(property => property.Name)
            .ToList();

        unfilledWorkerCounts.Should().BeEmpty();
    }
}
