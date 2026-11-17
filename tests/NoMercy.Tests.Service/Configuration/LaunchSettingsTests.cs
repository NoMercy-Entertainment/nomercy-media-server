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

namespace NoMercy.Tests.Service.Configuration;

/// <summary>
/// `dotnet run --project src/NoMercy.Service` (the README's build-from-source step) takes the first
/// launch profile. A new user must get a production server, never the maintainers' --dev mode.
/// </summary>
[Trait("Category", "Unit")]
public class LaunchSettingsTests
{
    private static JsonElement Profiles()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NoMercy.Server.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir.FullName, "src", "NoMercy.Service", "Properties", "launchSettings.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("profiles").Clone();
    }

    private static string Args(JsonElement profile) =>
        profile.TryGetProperty("commandLineArgs", out JsonElement args) ? args.GetString() ?? "" : "";

    private static bool IsDev(string args) =>
        args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a is "--dev" or "-d");

    [Fact]
    public void DotnetRun_DefaultProfile_IsNotDevMode()
    {
        JsonProperty first = Profiles()
            .EnumerateObject()
            .First(p => p.Value.GetProperty("commandName").GetString() == "Project");

        Assert.False(IsDev(Args(first.Value)), $"first launch profile '{first.Name}' runs --dev");
    }

    [Fact]
    public void MaintainerProfile_Https_KeepsDevMode()
    {
        Assert.True(IsDev(Args(Profiles().GetProperty("https"))));
    }
}
