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

using NoMercy.NmSystem.Dto;

namespace NoMercy.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Lists optical drives on macOS from <c>diskutil list</c>, then asks
/// <c>diskutil info</c> for the volume name and mount point. macOS only gives
/// an optical drive a BSD device node while a disc is inserted, so every drive
/// this scanner reports has a disc; an empty listing simply means no drive.
/// </summary>
public sealed class MacOpticalDriveScanner(ICommandRunner commandRunner) : IOpticalDriveScanner
{
    private const string Diskutil = "diskutil";
    private const string DeviceNodePrefix = "/dev/disk";
    private const string OpticalPartitionTypePrefix = "CD_";
    private const string NotApplicable = "Not applicable";
    private const string VolumeNameKey = "Volume Name";
    private const string MountPointKey = "Mount Point";

    public IReadOnlyList<OpticalDriveState> GetDrives()
    {
        List<OpticalDriveState> drives = [];

        foreach (DiskSection section in ParseSections(RunDiskutil("list")))
        {
            if (!section.IsOptical)
                continue;

            (string? label, string? mountPoint) = ProbeVolume(section.Identifiers);
            drives.Add(
                new(Path: section.DevicePath, Label: label, HasDisc: true, MountPoint: mountPoint)
            );
        }

        return drives;
    }

    /// <summary>
    /// Splits the listing into one section per <c>/dev/diskN</c> header. Rows
    /// look like <c>0:  CD_partition_scheme  *7.9 GB  disk4</c>; a disc shows a
    /// <c>CD_</c> partition type (CD_partition_scheme, CD_DA, CD_ROM_Mode_1).
    /// </summary>
    private static List<DiskSection> ParseSections(string listing)
    {
        List<DiskSection> sections = [];
        DiskSection? current = null;

        foreach (string line in SplitLines(listing))
        {
            if (line.StartsWith(DeviceNodePrefix, StringComparison.Ordinal))
            {
                current = new(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
                sections.Add(current);
                continue;
            }

            if (current is null)
                continue;

            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool isPartitionRow =
                tokens.Length >= 3 && char.IsDigit(tokens[0][0]) && tokens[0].EndsWith(':');
            if (!isPartitionRow)
                continue;

            if (
                tokens[1].StartsWith(OpticalPartitionTypePrefix, StringComparison.OrdinalIgnoreCase)
            )
                current.IsOptical = true;

            current.Identifiers.Add(tokens[^1]);
        }

        return sections;
    }

    /// <summary>
    /// The whole disk of a CD_partition_scheme has no filesystem; the label and
    /// mount point live on a slice (<c>disk4s0</c>). Prefer the first identifier
    /// that is mounted, otherwise the first that at least has a name.
    /// </summary>
    private (string? Label, string? MountPoint) ProbeVolume(IReadOnlyList<string> identifiers)
    {
        string? firstLabel = null;

        foreach (string identifier in identifiers)
        {
            Dictionary<string, string> info = ParseInfo(RunDiskutil("info", identifier));
            string? label = ValueOrNull(info, VolumeNameKey);
            string? mountPoint = ValueOrNull(info, MountPointKey);

            if (mountPoint is not null)
                return (label ?? firstLabel, mountPoint);

            firstLabel ??= label;
        }

        return (firstLabel, null);
    }

    /// <summary>
    /// <c>diskutil info</c> prints <c>   Key:   value</c> lines; anything without
    /// a separator (blank lines, the legacy "Unknown" sentinel) is skipped.
    /// </summary>
    private static Dictionary<string, string> ParseInfo(string output)
    {
        Dictionary<string, string> info = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in SplitLines(output))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
                info.TryAdd(key, value);
        }

        return info;
    }

    private static string? ValueOrNull(Dictionary<string, string> info, string key)
    {
        if (!info.TryGetValue(key, out string? value))
            return null;

        bool isBlank =
            value.Length == 0
            || value.StartsWith(NotApplicable, StringComparison.OrdinalIgnoreCase);
        return isBlank ? null : value;
    }

    private string RunDiskutil(params string[] arguments)
    {
        try
        {
            Shell.ExecResult result = commandRunner.Run(Diskutil, arguments);
            return result.Success ? result.StandardOutput : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Optical] diskutil {string.Join(' ', arguments)} failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static IEnumerable<string> SplitLines(string output) =>
        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'));

    private sealed class DiskSection(string devicePath)
    {
        public string DevicePath { get; } = devicePath;
        public bool IsOptical { get; set; }
        public List<string> Identifiers { get; } = [];
    }
}
