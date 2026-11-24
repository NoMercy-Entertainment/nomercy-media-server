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
namespace NoMercy.Cli.Commands;

/// <summary>
/// Centralizes the management HTTP route paths used by the CLI commands so the
/// paths are defined once instead of being repeated as magic strings across the
/// individual command handlers.
/// </summary>
internal static class ApiRoutes
{
    private const string ManageBase = "/manage";

    internal const string Update = ManageBase + "/update";
    internal const string Stop = ManageBase + "/stop";
    internal const string Restart = ManageBase + "/restart";
    internal const string LogLevel = ManageBase + "/loglevel";
    internal const string Config = ManageBase + "/config";
    internal const string Plugins = ManageBase + "/plugins";
    internal const string Status = ManageBase + "/status";
    internal const string Resources = ManageBase + "/resources";
    internal const string Queue = ManageBase + "/queue";
    internal const string AutoStart = ManageBase + "/autostart";
    internal const string Logs = ManageBase + "/logs";
    internal const string LogsStream = ManageBase + "/logs/stream";
}
