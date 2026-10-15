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

using System.Security.Cryptography;
using System.Text;
using NoMercy.NmSystem.Information;

namespace NoMercy.PluginSdk.Ipc;

/// <summary>
/// Where the server and one plugin's process meet.
/// <para>
/// Named per plugin, so one plugin can never reach another's channel, and the
/// two directions are separate endpoints: the broker is what the plugin calls
/// into, the host is what the server calls out to.
/// </para>
/// <para>
/// On Linux and macOS the endpoint is a unix socket, and a socket path has a
/// hard limit (<see cref="MaxSocketPathBytes" />). Under the data folder it
/// grew with the owner's home folder and broke a plugin on a long one. So the
/// sockets live in a short folder of their own, outside the data folder.
/// </para>
/// </summary>
public static class PluginChannelEndpoints
{
    /// <summary>
    /// The longest socket path that works everywhere. <c>sun_path</c> holds 104
    /// bytes on macOS and 108 on Linux, the terminating zero included, so 103
    /// is what fits on both.
    /// </summary>
    public const int MaxSocketPathBytes = 103;

    private const string RootPrefix = "nomercy-";

    private const int RandomLength = 12;

    // The longest name below the root: "/" + a plugin id + "/" + "broker.sock".
    private const int LongestTail = 1 + 26 + 1 + 11;

    private static readonly Lazy<string> SocketRoot = new(CreateSocketRoot);

    public static string RunDirectoryFor(Ulid pluginId) =>
        Path.Combine(SocketRoot.Value, pluginId.ToString());

    public static string BrokerFor(Ulid pluginId) => EndpointFor(pluginId, "broker");

    public static string HostFor(Ulid pluginId) => EndpointFor(pluginId, "host");

    private static string EndpointFor(Ulid pluginId, string role) =>
        Software.IsWindows
            ? $"NoMercy.Plugin.{pluginId}.{role}"
            : Path.Combine(RunDirectoryFor(pluginId), $"{role}.sock");

    /// <summary>
    /// The folder the root is made in: the user's runtime folder when there is
    /// one and it is short enough, otherwise <c>/tmp</c>.
    /// <para>
    /// <c>$XDG_RUNTIME_DIR</c> first because it belongs to this user alone and
    /// is emptied at logout. A service account often has none, and a runtime
    /// folder can be long, so <c>/tmp</c> is always the fallback. Never
    /// <c>$TMPDIR</c>: on macOS it is a path of fifty-odd bytes by itself.
    /// </para>
    /// </summary>
    internal static string ParentFor(string? runtimeDirectory, Func<string, bool> exists)
    {
        if (
            !string.IsNullOrWhiteSpace(runtimeDirectory)
            && Path.IsPathRooted(runtimeDirectory)
            && Fits(runtimeDirectory)
            && exists(runtimeDirectory)
        )
            return runtimeDirectory;

        return "/tmp";
    }

    /// <summary>Whether every socket under a root made in this folder stays inside the limit.</summary>
    internal static bool Fits(string parent) =>
        Encoding.UTF8.GetByteCount(Path.Combine(parent, RootName(new string('x', RandomLength))))
            + LongestTail
        <= MaxSocketPathBytes;

    /// <summary>
    /// A random name, never one anybody could guess. A predictable folder in a
    /// shared <c>/tmp</c> is one another account can make first and own, and
    /// then every socket the server puts in it is theirs to replace.
    /// </summary>
    internal static string RootName(string random) => RootPrefix + random;

    private static string CreateSocketRoot()
    {
        // OperatingSystem rather than Software: the analyzer that guards the
        // Unix-only calls below recognises only this one.
        if (OperatingSystem.IsWindows())
            return string.Empty;

        string parent = ParentFor(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Directory.Exists
        );
        string root = Path.Combine(
            parent,
            RootName(RandomNumberGenerator.GetHexString(RandomLength, lowercase: true))
        );

        // Owner only: the sockets in here hand out the owner's library, and
        // the token is the second lock on that door, not the only one.
        Directory.CreateDirectory(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );

        FileSystemInfo created = new DirectoryInfo(root);

        if (
            created.LinkTarget is not null
            || File.GetUnixFileMode(root)
                != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
        )
            throw new InvalidOperationException(
                $"The plugin socket folder {root} is not a private folder of this account. "
                    + "Plugins that run in their own process cannot start until it is."
            );

        return root;
    }
}
