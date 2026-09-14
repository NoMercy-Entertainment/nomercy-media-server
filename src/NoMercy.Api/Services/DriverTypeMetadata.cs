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

using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Dashboard;

namespace NoMercy.Api.Services;

/// <summary>
/// Static catalogue of supported storage driver types, their display names,
/// and the JSON config keys they accept. Used by both the folder-driver picker
/// and the named-driver admin page.
/// </summary>
public static class DriverTypeMetadata
{
    // Types that users are allowed to create through the dashboard.
    // "local" is included so users can register a named local-FS driver
    // pointing at e.g. an SMB/NFS mount surfaced by the OS (\\nas\share,
    // /mnt/movies). The single built-in system local driver remains
    // protected from user edit/delete.
    public static readonly string[] AllUserCreatable = ["local", "nfs", "s3", "r2", "webdav"];

    public static readonly DriverMetadataDto[] All =
    [
        new()
        {
            Type = "local",
            DisplayName = "Local filesystem",
            Available = true,
            ConfigSchema = new()
            {
                {
                    "rootPath",
                    @"string (required) — local filesystem path or UNC path, e.g. /mnt/movies or \\nas\share"
                },
            },
        },
        new()
        {
            Type = "nfs",
            DisplayName = "NFS",
            Available = true,
            ConfigSchema = new()
            {
                { "server", "string (required) — hostname or IP of NFS server" },
                { "export", "string (required) — export path, e.g. /exports/media" },
                { "version", "int? (default 3) — NFS version: 3 or 4" },
                { "uid", "int? — AUTH_UNIX user ID" },
                { "gid", "int? — AUTH_UNIX group ID" },
                { "port", "int? (default 2049) — NFS port" },
                { "mountPort", "int? — mount protocol port (NFS3 only)" },
            },
        },
        new()
        {
            Type = "s3",
            DisplayName = "S3-compatible",
            Available = true,
            ConfigSchema = new()
            {
                { "bucket", "string" },
                { "region", "string" },
                { "prefix", "string?" },
                { "credentialsRef", "string?" },
                { "endpoint", "string?" },
            },
        },
        new()
        {
            Type = "r2",
            DisplayName = "Cloudflare R2",
            Available = true,
            ConfigSchema = new()
            {
                { "bucket", "string" },
                { "region", "string" },
                { "prefix", "string?" },
                { "credentialsRef", "string?" },
                { "endpoint", "string (required for R2)" },
            },
        },
        new()
        {
            Type = "webdav",
            DisplayName = "WebDAV",
            Available = true,
            ConfigSchema = new()
            {
                {
                    "url",
                    "string (required) — base URL of the WebDAV collection, e.g. https://nextcloud.example.com/remote.php/dav/files/user/Movies/"
                },
                {
                    "ignoreCertErrors",
                    "bool? (default false) — skip TLS cert validation (opt-in for self-signed certs)"
                },
                { "timeoutSeconds", "int? (default 30) — HTTP request timeout" },
            },
        },
    ];

    public static string? ValidateConfig(string driverType, JObject? config)
    {
        switch (driverType)
        {
            // User-created local drivers must point at a real path so the
            // factory can resolve them. The single built-in system local
            // driver keeps an empty rootPath (passthrough mode) and is
            // protected from edit at the controller layer, so this check
            // only applies to user-managed rows.
            case "local":
                if (config is null)
                    return "config is required for 'local' and must include 'rootPath'.";

                string? localRoot = config["rootPath"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(localRoot))
                    return "config.rootPath must be a non-empty string for 'local'.";

                return null;

            case "nfs":
                if (config is null)
                    return "config is required for 'nfs' and must include 'server' and 'export'.";

                string? nfsServer = config["server"]?.Value<string>();
                string? nfsExport = config["export"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(nfsServer))
                    return "config.server must be a non-empty string for 'nfs'.";

                if (string.IsNullOrWhiteSpace(nfsExport))
                    return "config.export must be a non-empty string for 'nfs'.";

                int? nfsVersion = config["version"]?.Value<int?>();
                if (nfsVersion.HasValue && nfsVersion != 3 && nfsVersion != 4)
                    return "config.version must be 3 or 4 for 'nfs'.";

                return null;

            case "s3":
            case "r2":
                if (config is null)
                    return $"config is required for '{driverType}' and must include 'bucket' and 'region'.";

                string? bucket = config["bucket"]?.Value<string>();
                string? region = config["region"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(bucket))
                    return $"config.bucket must be a non-empty string for '{driverType}'.";

                if (string.IsNullOrWhiteSpace(region))
                    return $"config.region must be a non-empty string for '{driverType}'.";

                if (driverType == "r2")
                {
                    string? endpoint = config["endpoint"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(endpoint))
                        return "config.endpoint is required for 'r2' (set to your R2 endpoint URL).";
                }

                return null;

            case "webdav":
                if (config is null)
                    return "config is required for 'webdav' and must include 'url'.";

                string? webDavUrl = config["url"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(webDavUrl))
                    return "config.url must be a non-empty string for 'webdav'.";

                return null;

            default:
                return null;
        }
    }
}
