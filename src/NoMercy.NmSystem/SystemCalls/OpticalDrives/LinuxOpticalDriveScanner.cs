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

using System.Text.RegularExpressions;
using NoMercy.NmSystem.Dto;

namespace NoMercy.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Lists optical drives on Linux from <c>lsblk -P</c>, whose <c>KEY="value"</c>
/// pairs survive empty cells (the columnar form shifted LABEL into the
/// MOUNTPOINT column whenever a disc was not mounted). Only <c>TYPE="rom"</c>
/// rows are drives; matching an "sr" substring also caught partitions mounted
/// under /srv.
/// </summary>
public sealed partial class LinuxOpticalDriveScanner(ICommandRunner commandRunner)
    : IOpticalDriveScanner
{
    private const string Lsblk = "lsblk";
    private const string RomDeviceType = "rom";
    private const string NameKey = "NAME";
    private const string TypeKey = "TYPE";
    private const string MountPointKey = "MOUNTPOINT";
    private const string LabelKey = "LABEL";
    private const string FileSystemKey = "FSTYPE";

    private static readonly string[] _lsblkArguments =
    [
        "-P",
        "-o",
        $"{NameKey},{TypeKey},{MountPointKey},{LabelKey},{FileSystemKey}",
    ];

    public IReadOnlyList<OpticalDriveState> GetDrives()
    {
        List<OpticalDriveState> drives = [];

        foreach (string line in RunLsblk().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Dictionary<string, string> fields = ParsePairs(line);
            string? name = ValueOrNull(fields, NameKey);
            if (name is null || ValueOrNull(fields, TypeKey) != RomDeviceType)
                continue;

            string? label = ValueOrNull(fields, LabelKey);
            string? mountPoint = ValueOrNull(fields, MountPointKey);
            string? fileSystem = ValueOrNull(fields, FileSystemKey);
            bool hasDisc = label is not null || mountPoint is not null || fileSystem is not null;

            drives.Add(
                new(Path: $"/dev/{name}", Label: label, HasDisc: hasDisc, MountPoint: mountPoint)
            );
        }

        return drives;
    }

    private static Dictionary<string, string> ParsePairs(string line)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);

        foreach (Match match in PairRegex().Matches(line))
            fields[match.Groups["key"].Value] = DecodeEscapes(match.Groups["value"].Value);

        return fields;
    }

    /// <summary>lsblk -P writes a double quote as <c>\x22</c> and a backslash as <c>\x5c</c>.</summary>
    private static string DecodeEscapes(string value) =>
        value.Contains("\\x", StringComparison.Ordinal)
            ? HexEscapeRegex()
                .Replace(
                    value,
                    match => ((char)Convert.ToInt32(match.Groups["hex"].Value, 16)).ToString()
                )
            : value;

    private static string? ValueOrNull(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    private string RunLsblk()
    {
        try
        {
            Shell.ExecResult result = commandRunner.Run(Lsblk, _lsblkArguments);
            return result.Success ? result.StandardOutput : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Optical] lsblk failed: {ex.Message}");
            return string.Empty;
        }
    }

    [GeneratedRegex(
        "(?<key>[A-Z_:-]+)=\"(?<value>[^\"]*)\"",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PairRegex();

    [GeneratedRegex(
        @"\\x(?<hex>[0-9A-Fa-f]{2})",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex HexEscapeRegex();
}
