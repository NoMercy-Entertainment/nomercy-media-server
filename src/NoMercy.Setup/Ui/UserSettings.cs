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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Setup.Ui;

public static class UserSettings
{
    public static bool TryGetUserSettings(out Dictionary<string, string> settings)
    {
        settings = new();

        try
        {
            using AppDbContext appContext = new();
            List<Configuration> configuration = appContext.Configuration.ToList();

            // Compat backfill: ConnectivityMode's in-process default changed from Auto
            // to LocalOnly (a fresh install must not try WAN exposure before anyone
            // opted in). An existing install that never touched this setting has no
            // "connectivityMode" row and would silently fall through to the new
            // LocalOnly default on next boot, stopping port-forward/tunnel with no
            // warning. Evidence of a prior setup (an issued SSL certificate row) means
            // this install predates the change, so pin it to the old implicit default
            // explicitly. A genuinely fresh install has neither row and gets the new
            // safe default with no backfill.
            bool hasConnectivityModeRow = configuration.Any(c => c.Key == "connectivityMode");
            if (!hasConnectivityModeRow)
            {
                bool isExistingInstall = configuration.Any(c =>
                    c.Key == "ssl_certificate" && !string.IsNullOrEmpty(c.SecureValue)
                );
                if (isExistingInstall)
                {
                    Configuration backfillRow = new()
                    {
                        Key = "connectivityMode",
                        Value = nameof(ConnectivityMode.Auto),
                    };
                    appContext
                        .Configuration.Upsert(
                            new() { Key = backfillRow.Key, Value = backfillRow.Value }
                        )
                        .On(c => c.Key)
                        .Run();
                    configuration.Add(backfillRow);

                    Logger.App(
                        "UserSettings: legacy install detected (SSL certificate on file, no "
                            + "connectivityMode row) — backfilling connectivityMode=Auto to "
                            + "preserve prior WAN-exposure behavior",
                        LogEventLevel.Information
                    );
                }
            }

            foreach (Configuration config in configuration)
            {
                if (config.Key is "internalPort" or "externalPort")
                {
                    if (!int.TryParse(config.Value, out int parsedPort))
                    {
                        Logger.App(
                            $"UserSettings: skipping malformed '{config.Key}' value '{config.Value}' — keeping other settings",
                            LogEventLevel.Warning
                        );
                        continue;
                    }

                    if (
                        config.Key == "internalPort"
                        && RuntimeServerSettings.Current.InternalServerPort != parsedPort
                    )
                    {
                        config.Value = RuntimeServerSettings.Current.InternalServerPort.ToString();
                        appContext
                            .Configuration.Upsert(new() { Key = config.Key, Value = config.Value })
                            .On(c => c.Key)
                            .Run();
                    }
                    else if (
                        config.Key == "externalPort"
                        && RuntimeServerSettings.Current.ExternalServerPort != parsedPort
                    )
                    {
                        config.Value = RuntimeServerSettings.Current.ExternalServerPort.ToString();
                        appContext
                            .Configuration.Upsert(new() { Key = config.Key, Value = config.Value })
                            .On(c => c.Key)
                            .Run();
                    }
                }

                settings[config.Key] = config.Value;
            }

            Logger.App(
                $"UserSettings: loaded {settings.Count} key(s) from Configuration table",
                LogEventLevel.Information
            );
            return true;
        }
        catch (Exception ex)
        {
            // Silent failure here is the original "worker counts reset on
            // boot" bug — every saved value gets discarded if the read throws
            // (schema not yet ready, SecureValue conversion error, etc.).
            // Surface the cause so operators can act.
            Logger.App(
                $"UserSettings: failed to read Configuration table — using defaults this boot. "
                    + $"{ex.GetType().Name}: {ex.Message}",
                LogEventLevel.Error
            );
            return false;
        }
    }

    private static bool _configDumpLogged;

    public static void ApplySettings(Dictionary<string, string> settings, bool silent = false)
    {
        // Dump the resolved config once per process at Verbose, with secret
        // values redacted. ApplySettings runs more than once during boot, so
        // without the guard the whole Configuration table is logged every pass.
        bool dumpConfig = !silent && !_configDumpLogged;
        if (dumpConfig)
            _configDumpLogged = true;

        // Emit the resolved config as a compact 4-column table (one log entry,
        // rendered under the gutter). Secret values are skipped entirely since
        // they must not be written to logs. Logged once per process at Verbose.
        if (dumpConfig)
        {
            List<string> cells = new();
            foreach (KeyValuePair<string, string> entry in settings)
            {
                bool isSecret =
                    entry.Key.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.Contains("ssl_", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.Contains("fingerprint", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.Contains("secret", StringComparison.OrdinalIgnoreCase);
                if (isSecret)
                    continue;
                cells.Add($"{entry.Key} = {entry.Value}");
            }

            if (cells.Count > 0)
            {
                const int columns = 3;
                int rows = (cells.Count + columns - 1) / columns;

                // Per-column width keeps each column as narrow as its content.
                int[] columnWidth = new int[columns];
                for (int i = 0; i < cells.Count; i++)
                {
                    int col = i % columns;
                    if (cells[i].Length > columnWidth[col])
                        columnWidth[col] = cells[i].Length;
                }

                System.Text.StringBuilder builder = new();
                builder.Append($"Configuration ({cells.Count} key(s)):");
                for (int row = 0; row < rows; row++)
                {
                    builder.Append('\n').Append("  ");
                    for (int col = 0; col < columns; col++)
                    {
                        int index = (row * columns) + col;
                        if (index >= cells.Count)
                            break;

                        bool last = col == columns - 1 || index == cells.Count - 1;
                        builder.Append(
                            last ? cells[index] : cells[index].PadRight(columnWidth[col] + 2)
                        );
                    }
                }

                Logger.App(builder.ToString(), LogEventLevel.Verbose);
            }
        }

        using AppDbContext appContext = new();
        foreach (KeyValuePair<string, string> setting in settings)
        {
            switch (setting.Key)
            {
                case "internalPort":
                    if (!int.TryParse(setting.Value, out int internalPort))
                    {
                        Logger.App(
                            $"UserSettings: skipping malformed 'internalPort' value '{setting.Value}' — keeping current runtime setting",
                            LogEventLevel.Warning
                        );
                        break;
                    }

                    bool internalPortChanged =
                        RuntimeServerSettings.Current.InternalServerPort != internalPort;
                    RuntimeServerSettings.Current.InternalServerPort = internalPort;
                    if (internalPortChanged)
                        appContext
                            .Configuration.Upsert(
                                new()
                                {
                                    Key = setting.Key,
                                    Value =
                                        RuntimeServerSettings.Current.InternalServerPort.ToString(),
                                }
                            )
                            .On(c => c.Key)
                            .Run();
                    break;
                case "externalPort":
                    if (!int.TryParse(setting.Value, out int externalPort))
                    {
                        Logger.App(
                            $"UserSettings: skipping malformed 'externalPort' value '{setting.Value}' — keeping current runtime setting",
                            LogEventLevel.Warning
                        );
                        break;
                    }

                    bool externalPortChanged =
                        RuntimeServerSettings.Current.ExternalServerPort != externalPort;
                    RuntimeServerSettings.Current.ExternalServerPort = externalPort;
                    if (externalPortChanged)
                        appContext
                            .Configuration.Upsert(
                                new()
                                {
                                    Key = setting.Key,
                                    Value =
                                        RuntimeServerSettings.Current.ExternalServerPort.ToString(),
                                }
                            )
                            .On(c => c.Key)
                            .Run();
                    break;
                case "libraryRunners":
                    RuntimeServerSettings.Current.LibraryWorkers = new(
                        RuntimeServerSettings.Current.LibraryWorkers.Key,
                        setting.Value.ToInt()
                    );
                    break;
                case "importRunners" or "queueRunners":
                    RuntimeServerSettings.Current.ImportWorkers = new(
                        RuntimeServerSettings.Current.ImportWorkers.Key,
                        setting.Value.ToInt()
                    );
                    break;
                case "extrasRunners" or "dataRunners":
                    RuntimeServerSettings.Current.ExtrasWorkers = new(
                        RuntimeServerSettings.Current.ExtrasWorkers.Key,
                        setting.Value.ToInt()
                    );
                    break;
                case "encoderRunners":
                    RuntimeServerSettings.Current.EncoderWorkers = new(
                        RuntimeServerSettings.Current.EncoderWorkers.Key,
                        setting.Value.ToInt()
                    );
                    Logger.App(
                        $"UserSettings: RuntimeServerSettings.Current.EncoderWorkers loaded as {RuntimeServerSettings.Current.EncoderWorkers.Value} (DB value '{setting.Value}')"
                    );
                    break;
                case "cronRunners":
                    RuntimeServerSettings.Current.CronWorkers = new(
                        RuntimeServerSettings.Current.CronWorkers.Key,
                        setting.Value.ToInt()
                    );
                    break;
                case "imageRunners":
                    RuntimeServerSettings.Current.ImageWorkers = new(
                        RuntimeServerSettings.Current.ImageWorkers.Key,
                        setting.Value.ToInt()
                    );
                    break;
                case "fileRunners":
                    RuntimeServerSettings.Current.FileWorkers = new(
                        RuntimeServerSettings.Current.FileWorkers.Key,
                        setting.Value.ToInt()
                    );
                    break;
                case "musicRunners":
                    RuntimeServerSettings.Current.MusicWorkers = new(
                        RuntimeServerSettings.Current.MusicWorkers.Key,
                        setting.Value.ToInt()
                    );
                    break;
                case "swagger":
                    RuntimeServerSettings.Current.Swagger = setting.Value.ToBoolean();
                    break;
                case "UseSynthesizedDns":
                    RuntimeServerSettings.Current.UseSynthesizedDns = setting.Value.ToBoolean();
                    break;
                case "allowAdultContent":
                    RuntimeServerSettings.Current.AllowAdultContent = setting.Value.ToBoolean();
                    break;
                case "connectivityMode":
                    if (Enum.TryParse(setting.Value, true, out ConnectivityMode connectivityMode))
                        RuntimeServerSettings.Current.ConnectivityMode = connectivityMode;
                    break;
                case "derivedAudioCapGb":
                    if (!int.TryParse(setting.Value, out int derivedAudioCapGb))
                    {
                        Logger.App(
                            $"UserSettings: skipping malformed 'derivedAudioCapGb' value '{setting.Value}' — keeping current runtime setting",
                            LogEventLevel.Warning
                        );
                        break;
                    }

                    RuntimeServerSettings.Current.DerivedAudioCapBytes =
                        derivedAudioCapGb * 1024L * 1024 * 1024;
                    break;
            }
        }
    }
}
