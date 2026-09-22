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

namespace NoMercy.Plugins.OutOfProcess;

/// <summary>
/// The macOS sandbox profile for one plugin.
/// <para>
/// Separate from the sandbox that writes it so the profile is checked on every
/// machine that builds this, not only on a Mac. A rule that named the wrong
/// path, or left one out, is a plugin reading the owner's whole disk on a
/// kernel that reported no error.
/// </para>
/// <para>
/// Deny first, then name what is allowed. A profile that started from allow
/// would grant every capability nobody thought to write a rule against, which
/// is the opposite of what the owner consented to.
/// </para>
/// </summary>
public static class MacSandboxProfile
{
    /// <summary>
    /// Everything a process needs to start at all: the loader, the shared
    /// cache and the system frameworks. Without these the plugin is not
    /// confined, it simply never runs.
    /// </summary>
    private static readonly string[] SystemReads = ["/usr/lib", "/usr/bin", "/bin", "/System"];

    public static string For(string dataFolder, bool allowsSpawn, string executable)
    {
        List<string> lines =
        [
            "(version 1)",
            "(import \"bsd.sb\")",
            "(deny default)",
            "(allow process-fork)",
            "(allow file-read-metadata)",
            $"(allow file-read* {Subpaths(SystemReads)})",
            $"(allow file-read* {Subpaths(Locations(dataFolder))})",
            $"(allow file-write* {Subpaths(Locations(dataFolder))})",
        ];

        // sandbox-exec applies the profile and THEN runs its target, so a
        // profile that does not name the target is a plugin that never starts.
        lines.Add($"(allow process-exec (literal \"{executable}\"))");

        // A plugin without process.spawn cannot execute anything else even if it
        // finds a binary, so the grant and the profile say the same thing and
        // the kernel is the one enforcing it.
        if (allowsSpawn)
            lines.Add($"(allow process-exec {Subpaths(Locations(dataFolder))})");

        return string.Join('\n', lines) + '\n';
    }

    /// <summary>
    /// The path as written and, where macOS keeps the real one behind a
    /// firmlink, its resolved twin. A profile naming <c>/var/folders/...</c>
    /// matches nothing, because the kernel checks the resolved
    /// <c>/private/var/folders/...</c> and a rule that matches nothing reads
    /// exactly like a rule that was never needed.
    /// </summary>
    public static IReadOnlyList<string> Locations(string path)
    {
        string[] firmlinked = ["/var/", "/tmp/"];

        foreach (string prefix in firmlinked)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return [path, "/private" + path];

            if (path.StartsWith("/private" + prefix, StringComparison.Ordinal))
                return [path, path["/private".Length..]];
        }

        return [path];
    }

    private static string Subpaths(IReadOnlyList<string> paths) =>
        string.Join(' ', paths.Select(path => $"(subpath \"{path}\")"));
}
