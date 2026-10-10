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
using NoMercy.Launcher.Models;
using Xunit;

namespace NoMercy.Tests.Launcher.Models;

/// <summary>
/// REQUIREMENT: these DTOs are the wire contract between the Launcher (a
/// separate GUI process) and the server's <c>/manage/*</c> endpoints. A wrong
/// <c>[JsonProperty]</c> name deserializes to a silent default, not an error —
/// the same wire-format-mismatch class of bug documented for the KMP client.
/// Each test here round-trips a real snake_case JSON payload (the actual shape
/// the server sends) through <c>Newtonsoft.Json</c> and asserts every property
/// landed, so a renamed/typo'd JsonProperty attribute fails loudly here instead
/// of showing up as "field silently stuck at its default" in the running app.
/// <see cref="NoMercy.Tests.Service.ServerStatusResponseTests"/> already covers
/// the core status fields; this file covers the models that had zero coverage
/// (and the <see cref="ServerStatusResponse"/> fields that test omits).
/// </summary>
public sealed class JsonWireFormatTests
{
    [Fact]
    public void ServerConfigResponse_Deserialize_MapsEveryProperty()
    {
        string json = """
            {
                "internal_port": 7626,
                "external_port": 7627,
                "server_name": "nomercy-test",
                "library_workers": 1,
                "import_workers": 2,
                "extras_workers": 15,
                "encoder_workers": 1,
                "cron_workers": 1,
                "image_workers": 10,
                "file_workers": 4,
                "music_workers": 2,
                "swagger": true
            }
            """;

        ServerConfigResponse? result = JsonConvert.DeserializeObject<ServerConfigResponse>(json);

        result.Should().NotBeNull();
        result!.InternalPort.Should().Be(7626);
        result.ExternalPort.Should().Be(7627);
        result.ServerName.Should().Be("nomercy-test");
        result.LibraryWorkers.Should().Be(1);
        result.ImportWorkers.Should().Be(2);
        result.ExtrasWorkers.Should().Be(15);
        result.EncoderWorkers.Should().Be(1);
        result.CronWorkers.Should().Be(1);
        result.ImageWorkers.Should().Be(10);
        result.FileWorkers.Should().Be(4);
        result.MusicWorkers.Should().Be(2);
        result.Swagger.Should().BeTrue();
    }

    [Fact]
    public void ServerConfigResponse_Serialize_UsesSnakeCaseWireNames()
    {
        ServerConfigResponse config = new()
        {
            InternalPort = 1,
            ExternalPort = 2,
            LibraryWorkers = 3,
        };

        string json = JsonConvert.SerializeObject(config);

        json.Should().Contain("\"internal_port\":1");
        json.Should().Contain("\"external_port\":2");
        json.Should().Contain("\"library_workers\":3");
    }

    [Fact]
    public void TraySettings_Deserialize_MapsEveryProperty()
    {
        string json = """
            {
                "show_on_startup": true,
                "startup_arguments": "--dev --port 7626",
                "auto_start": true
            }
            """;

        TraySettings? result = JsonConvert.DeserializeObject<TraySettings>(json);

        result.Should().NotBeNull();
        result!.ShowOnStartup.Should().BeTrue();
        result.StartupArguments.Should().Be("--dev --port 7626");
        result.AutoStart.Should().BeTrue();
    }

    [Fact]
    public void TraySettings_Deserialize_MissingFields_UsesDefaults()
    {
        TraySettings? result = JsonConvert.DeserializeObject<TraySettings>("{}");

        result.Should().NotBeNull();
        result!.ShowOnStartup.Should().BeFalse();
        result.StartupArguments.Should().Be(string.Empty);
        result.AutoStart.Should().BeFalse();
    }

    [Fact]
    public void UpdateCheckResult_Deserialize_MapsEveryProperty()
    {
        string json = """
            {
                "status": "available",
                "message": "A new version is ready",
                "use_installer": true,
                "latest_version": "2.1.0",
                "path": "C:\\staged\\update.exe"
            }
            """;

        UpdateCheckResult? result = JsonConvert.DeserializeObject<UpdateCheckResult>(json);

        result.Should().NotBeNull();
        result!.Status.Should().Be("available");
        result.Message.Should().Be("A new version is ready");
        result.UseInstaller.Should().BeTrue();
        result.LatestVersion.Should().Be("2.1.0");
        result.Path.Should().Be("C:\\staged\\update.exe");
    }

    [Fact]
    public void ActivityInfo_Deserialize_MapsEveryProperty()
    {
        string json = """
            {
                "active_streams": 3,
                "active_encodes": 1,
                "can_interrupt_safely": false
            }
            """;

        ActivityInfo? result = JsonConvert.DeserializeObject<ActivityInfo>(json);

        result.Should().NotBeNull();
        result!.ActiveStreams.Should().Be(3);
        result.ActiveEncodes.Should().Be(1);
        result.CanInterruptSafely.Should().BeFalse();
    }

    [Fact]
    public void LogEntryResponse_Deserialize_MapsEveryProperty()
    {
        string json = """
            {
                "type": "Server",
                "message": "listening on port 7626",
                "color": "#22C55E",
                "threadId": 12,
                "time": "2026-01-01T12:00:00Z",
                "level": "Information"
            }
            """;

        LogEntryResponse? result = JsonConvert.DeserializeObject<LogEntryResponse>(json);

        result.Should().NotBeNull();
        result!.Type.Should().Be("Server");
        result.Message.Should().Be("listening on port 7626");
        result.Color.Should().Be("#22C55E");
        result.ThreadId.Should().Be(12);
        result.Time.Should().Be(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        result.Level.Should().Be("Information");
    }

    /// <summary>
    /// The fields NoMercy.Tests.Service's ServerStatusResponseTests does not
    /// already cover: auto_start, update_available, restart_needed,
    /// latest_version, setup_phase, internal_address, external_address, and
    /// the nested app_status object.
    /// </summary>
    [Fact]
    public void ServerStatusResponse_Deserialize_MapsUpdateAndSetupFields()
    {
        string json = """
            {
                "status": "starting",
                "auto_start": true,
                "update_available": true,
                "restart_needed": true,
                "latest_version": "2.1.0",
                "setup_phase": "Registering",
                "setup_label": "Connecting to NoMercy",
                "setup_detail": "Registering server and securing your connection... (this can take up to 10 minutes)",
                "internal_address": "https://192.168.1.10:7626",
                "external_address": "https://my-server.nomercy.tv",
                "app_status": { "running": true, "pid": 4242 }
            }
            """;

        ServerStatusResponse? result = JsonConvert.DeserializeObject<ServerStatusResponse>(json);

        result.Should().NotBeNull();
        result!.AutoStart.Should().BeTrue();
        result.UpdateAvailable.Should().BeTrue();
        result.RestartNeeded.Should().BeTrue();
        result.LatestVersion.Should().Be("2.1.0");
        result.SetupPhase.Should().Be("Registering");
        result.SetupLabel.Should().Be("Connecting to NoMercy");
        result.SetupDetail.Should().Contain("up to 10 minutes");
        result.InternalAddress.Should().Be("https://192.168.1.10:7626");
        result.ExternalAddress.Should().Be("https://my-server.nomercy.tv");
        result.AppStatus.Should().NotBeNull();
        result.AppStatus!.Running.Should().BeTrue();
        result.AppStatus.Pid.Should().Be(4242);
    }
}
