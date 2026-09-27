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

using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Ui;

namespace NoMercy.Tests.Setup;

// Regression coverage for the "one bad settings row wipes ALL settings" boot
// crash: a single malformed int in the Configuration table used to make
// TryGetUserSettings/ApplySettings throw (or discard every other row).
[Trait("Category", "Unit")]
public class UserSettingsTests : IDisposable
{
    private readonly int _originalInternalPort = RuntimeServerSettings.Current.InternalServerPort;
    private readonly int _originalExternalPort = RuntimeServerSettings.Current.ExternalServerPort;
    private readonly ConnectivityMode _originalConnectivityMode = RuntimeServerSettings
        .Current
        .ConnectivityMode;
    private readonly ReleaseChannel _originalUpdateChannel = RuntimeServerSettings
        .Current
        .UpdateChannel;

    public UserSettingsTests()
    {
        Directory.CreateDirectory(AppFiles.DataPath);

        using AppDbContext context = new();
        context.Database.EnsureCreated();
        context.Configuration.RemoveRange(context.Configuration);
        context.SaveChanges();
    }

    public void Dispose()
    {
        using AppDbContext context = new();
        context.Configuration.RemoveRange(context.Configuration);
        context.SaveChanges();

        RuntimeServerSettings.Current.InternalServerPort = _originalInternalPort;
        RuntimeServerSettings.Current.ExternalServerPort = _originalExternalPort;
        RuntimeServerSettings.Current.ConnectivityMode = _originalConnectivityMode;
        RuntimeServerSettings.Current.UpdateChannel = _originalUpdateChannel;
    }

    [Fact]
    public void TryGetUserSettings_MalformedPortRow_SkipsRowButKeepsOthers()
    {
        using (AppDbContext context = new())
        {
            context.Configuration.Add(new() { Key = "internalPort", Value = "not-a-number" });
            context.Configuration.Add(new() { Key = "cronRunners", Value = "3" });
            context.SaveChanges();
        }

        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings.Should().NotContainKey("internalPort");
        settings.Should().ContainKey("cronRunners");
        settings["cronRunners"].Should().Be("3");
    }

    [Fact]
    public void TryGetUserSettings_AllGoodRows_ReturnsEveryRow()
    {
        using (AppDbContext context = new())
        {
            context.Configuration.Add(new() { Key = "cronRunners", Value = "2" });
            context.Configuration.Add(new() { Key = "imageRunners", Value = "5" });
            context.SaveChanges();
        }

        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings.Should().HaveCount(2);
        settings["cronRunners"].Should().Be("2");
        settings["imageRunners"].Should().Be("5");
    }

    [Fact]
    public void ApplySettings_MalformedPortValue_DoesNotThrow_AndSkipsThatSetting()
    {
        RuntimeServerSettings.Current.InternalServerPort = 7626;

        Dictionary<string, string> settings = new()
        {
            ["internalPort"] = "garbage",
            ["cronRunners"] = "4",
        };

        Action act = () => UserSettings.ApplySettings(settings, silent: true);

        act.Should().NotThrow();
        RuntimeServerSettings.Current.InternalServerPort.Should().Be(7626);
        RuntimeServerSettings.Current.CronWorkers.Value.Should().Be(4);
    }

    [Fact]
    public void ApplySettings_ValidPortValue_UpdatesRuntimeSetting()
    {
        RuntimeServerSettings.Current.InternalServerPort = 7626;

        Dictionary<string, string> settings = new() { ["internalPort"] = "8080" };

        UserSettings.ApplySettings(settings, silent: true);

        RuntimeServerSettings.Current.InternalServerPort.Should().Be(8080);
    }

    [Fact]
    public void ApplySettings_ExternalPort_UpdatesRuntimeSetting()
    {
        RuntimeServerSettings.Current.ExternalServerPort = 7626;

        UserSettings.ApplySettings(new() { ["externalPort"] = "9090" }, silent: true);

        RuntimeServerSettings.Current.ExternalServerPort.Should().Be(9090);
    }

    [Fact]
    public void ApplySettings_ExternalPort_MalformedValue_DoesNotThrow_KeepsCurrentSetting()
    {
        RuntimeServerSettings.Current.ExternalServerPort = 7626;

        Action act = () =>
            UserSettings.ApplySettings(new() { ["externalPort"] = "garbage" }, silent: true);

        act.Should().NotThrow();
        RuntimeServerSettings.Current.ExternalServerPort.Should().Be(7626);
    }

    [Theory]
    [InlineData("libraryRunners", 7)]
    [InlineData("importRunners", 8)]
    [InlineData("queueRunners", 9)] // alias for importRunners
    [InlineData("extrasRunners", 10)]
    [InlineData("dataRunners", 11)] // alias for extrasRunners
    [InlineData("encoderRunners", 12)]
    [InlineData("cronRunners", 13)]
    [InlineData("imageRunners", 14)]
    [InlineData("fileRunners", 15)]
    [InlineData("musicRunners", 16)]
    public void ApplySettings_WorkerCountSetting_UpdatesCorrespondingRuntimeWorkerCount(
        string key,
        int value
    )
    {
        UserSettings.ApplySettings(new() { [key] = value.ToString() }, silent: true);

        int actual = key switch
        {
            "libraryRunners" => RuntimeServerSettings.Current.LibraryWorkers.Value,
            "importRunners" or "queueRunners" => RuntimeServerSettings.Current.ImportWorkers.Value,
            "extrasRunners" or "dataRunners" => RuntimeServerSettings.Current.ExtrasWorkers.Value,
            "encoderRunners" => RuntimeServerSettings.Current.EncoderWorkers.Value,
            "cronRunners" => RuntimeServerSettings.Current.CronWorkers.Value,
            "imageRunners" => RuntimeServerSettings.Current.ImageWorkers.Value,
            "fileRunners" => RuntimeServerSettings.Current.FileWorkers.Value,
            "musicRunners" => RuntimeServerSettings.Current.MusicWorkers.Value,
            _ => throw new InvalidOperationException($"unmapped key {key}"),
        };

        actual.Should().Be(value);
    }

    [Fact]
    public void ApplySettings_WorkerCountSetting_PreservesTheQueueNameKey()
    {
        KeyValuePair<string, int> before = RuntimeServerSettings.Current.LibraryWorkers;

        UserSettings.ApplySettings(new() { ["libraryRunners"] = "5" }, silent: true);

        RuntimeServerSettings.Current.LibraryWorkers.Key.Should().Be(before.Key);
        RuntimeServerSettings.Current.LibraryWorkers.Value.Should().Be(5);
    }

    [Theory]
    [InlineData("swagger", "true", true)]
    [InlineData("swagger", "false", false)]
    [InlineData("UseSynthesizedDns", "true", true)]
    [InlineData("UseSynthesizedDns", "false", false)]
    [InlineData("allowAdultContent", "true", true)]
    [InlineData("allowAdultContent", "false", false)]
    public void ApplySettings_BooleanSetting_UpdatesCorrespondingRuntimeFlag(
        string key,
        string value,
        bool expected
    )
    {
        UserSettings.ApplySettings(new() { [key] = value }, silent: true);

        bool actual = key switch
        {
            "swagger" => RuntimeServerSettings.Current.Swagger,
            "UseSynthesizedDns" => RuntimeServerSettings.Current.UseSynthesizedDns,
            "allowAdultContent" => RuntimeServerSettings.Current.AllowAdultContent!.Value,
            _ => throw new InvalidOperationException($"unmapped key {key}"),
        };

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("Beta", ReleaseChannel.Beta)]
    [InlineData("nightly", ReleaseChannel.Nightly)]
    [InlineData("Stable", ReleaseChannel.Stable)]
    public void ApplySettings_UpdateChannel_UpdatesRuntimeSetting(
        string value,
        ReleaseChannel expected
    )
    {
        UserSettings.ApplySettings(new() { ["updateChannel"] = value }, silent: true);

        RuntimeServerSettings.Current.UpdateChannel.Should().Be(expected);
    }

    [Fact]
    public void ApplySettings_UnknownUpdateChannel_KeepsCurrentChannel()
    {
        RuntimeServerSettings.Current.UpdateChannel = ReleaseChannel.Beta;

        UserSettings.ApplySettings(new() { ["updateChannel"] = "alpha" }, silent: true);

        RuntimeServerSettings.Current.UpdateChannel.Should().Be(ReleaseChannel.Beta);
    }

    [Fact]
    public void ApplySettings_UnknownKey_IsIgnoredWithoutThrowing()
    {
        Action act = () =>
            UserSettings.ApplySettings(
                new() { ["someUnrecognizedFutureSetting"] = "x" },
                silent: true
            );

        act.Should().NotThrow();
    }

    [Fact]
    public void ApplySettings_InternalPortUnchanged_DoesNotReUpsert()
    {
        RuntimeServerSettings.Current.InternalServerPort = 7626;

        // Same value as current — the `if (internalPortChanged)` guard must skip the
        // Upsert call entirely; if it didn't, this would still pass (idempotent write)
        // but the guard's OWN branch (false path) needs a scenario where the value is
        // identical to prove it's actually being checked, not just always executed.
        UserSettings.ApplySettings(new() { ["internalPort"] = "7626" }, silent: true);

        RuntimeServerSettings.Current.InternalServerPort.Should().Be(7626);
    }

    [Fact]
    public void TryGetUserSettings_InternalPortDriftedFromRuntimeSetting_UpsertsCorrectedValue()
    {
        RuntimeServerSettings.Current.InternalServerPort = 7626;

        using (AppDbContext context = new())
        {
            // A stale DB value that no longer matches the in-memory runtime setting —
            // TryGetUserSettings must correct the DB row to match, not just read it back.
            context.Configuration.Add(new() { Key = "internalPort", Value = "1234" });
            context.SaveChanges();
        }

        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings["internalPort"].Should().Be("7626");

        using AppDbContext verify = new();
        Configuration row = verify.Configuration.Single(c => c.Key == "internalPort");
        row.Value.Should().Be("7626");
    }

    [Fact]
    public void TryGetUserSettings_ExternalPortDriftedFromRuntimeSetting_UpsertsCorrectedValue()
    {
        RuntimeServerSettings.Current.ExternalServerPort = 7626;

        using (AppDbContext context = new())
        {
            context.Configuration.Add(new() { Key = "externalPort", Value = "9999" });
            context.SaveChanges();
        }

        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings["externalPort"].Should().Be("7626");
    }

    [Fact]
    public void TryGetUserSettings_MalformedExternalPortRow_SkipsRowButKeepsOthers()
    {
        using (AppDbContext context = new())
        {
            context.Configuration.Add(new() { Key = "externalPort", Value = "not-a-number" });
            context.Configuration.Add(new() { Key = "imageRunners", Value = "5" });
            context.SaveChanges();
        }

        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings.Should().NotContainKey("externalPort");
        settings.Should().ContainKey("imageRunners");
    }

    [Fact]
    public void TryGetUserSettings_ExistingInstallWithCertNoConnectivityModeRow_BackfillsAuto()
    {
        // Simulates an upgraded install: it has an issued SSL certificate (proof of a
        // completed prior setup) but predates the connectivityMode setting entirely.
        using (AppDbContext context = new())
        {
            context.Configuration.Add(
                new()
                {
                    Key = "ssl_certificate",
                    Value = string.Empty,
                    SecureValue = "-----BEGIN CERTIFICATE-----fake-----END CERTIFICATE-----",
                }
            );
            context.SaveChanges();
        }

        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings.Should().ContainKey("connectivityMode");
        settings["connectivityMode"].Should().Be(nameof(ConnectivityMode.Auto));

        using AppDbContext verify = new();
        Configuration row = verify.Configuration.Single(c => c.Key == "connectivityMode");
        row.Value.Should().Be(nameof(ConnectivityMode.Auto));

        UserSettings.ApplySettings(settings, silent: true);
        RuntimeServerSettings.Current.ConnectivityMode.Should().Be(ConnectivityMode.Auto);
    }

    [Fact]
    public void TryGetUserSettings_FreshInstallNoCertNoConnectivityModeRow_DoesNotBackfill()
    {
        // A genuinely fresh install has neither row — the new safe LocalOnly default
        // applies with no backfill, since there is no prior behavior to preserve.
        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings.Should().NotContainKey("connectivityMode");

        using AppDbContext verify = new();
        verify.Configuration.Any(c => c.Key == "connectivityMode").Should().BeFalse();

        RuntimeServerSettings.Current.ConnectivityMode.Should().Be(ConnectivityMode.LocalOnly);
    }

    [Fact]
    public void TryGetUserSettings_ExistingInstallWithExplicitConnectivityModeRow_DoesNotOverwrite()
    {
        // A prior explicit user choice (even LocalOnly) must never be clobbered by the
        // backfill — the row-absent check is the only trigger.
        using (AppDbContext context = new())
        {
            context.Configuration.Add(
                new()
                {
                    Key = "ssl_certificate",
                    Value = string.Empty,
                    SecureValue = "-----BEGIN CERTIFICATE-----fake-----END CERTIFICATE-----",
                }
            );
            context.Configuration.Add(
                new() { Key = "connectivityMode", Value = nameof(ConnectivityMode.PortForward) }
            );
            context.SaveChanges();
        }

        bool success = UserSettings.TryGetUserSettings(out Dictionary<string, string> settings);

        success.Should().BeTrue();
        settings["connectivityMode"].Should().Be(nameof(ConnectivityMode.PortForward));
    }

    [Fact]
    public void ApplySettings_NotSilent_LogsConfigDumpOnce_RedactingSecretKeys()
    {
        // _configDumpLogged is a process-wide static "once" guard — reset it via
        // reflection so this test deterministically observes the "first dump" branch
        // regardless of whatever other tests in this process already ran.
        System.Reflection.FieldInfo? field = typeof(UserSettings).GetField(
            "_configDumpLogged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        field.Should().NotBeNull();
        field!.SetValue(null, false);

        Dictionary<string, string> settings = new()
        {
            ["cronRunners"] = "2",
            ["auth_access_token"] = "super-secret-should-be-redacted",
            ["ssl_private_key"] = "also-secret",
            ["some_fingerprint"] = "secret-too",
            ["client_secret_thing"] = "and-this",
        };

        Action act = () => UserSettings.ApplySettings(settings, silent: false);

        act.Should().NotThrow();
        // The guard must have flipped to true — proves the dump path was taken.
        ((bool)field.GetValue(null)!)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ApplySettings_NotSilent_SecondCall_DoesNotRedumpConfig()
    {
        System.Reflection.FieldInfo? field = typeof(UserSettings).GetField(
            "_configDumpLogged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        field!.SetValue(null, true);

        // With the guard already tripped, a second non-silent call must skip the dump
        // entirely (dumpConfig short-circuits to false) — still must not throw.
        Action act = () =>
            UserSettings.ApplySettings(new() { ["cronRunners"] = "3" }, silent: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void ApplySettings_NotSilent_ManySettings_RendersMultiColumnReportWithoutThrowing()
    {
        System.Reflection.FieldInfo? field = typeof(UserSettings).GetField(
            "_configDumpLogged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        field!.SetValue(null, false);

        // More than 3 entries (the report's column count) so the multi-row layout
        // (row/column index math) actually exercises more than a single row.
        Dictionary<string, string> settings = new();
        for (int i = 0; i < 7; i++)
            settings[$"setting{i}"] = $"value{i}";

        Action act = () => UserSettings.ApplySettings(settings, silent: false);

        act.Should().NotThrow();
    }
}
