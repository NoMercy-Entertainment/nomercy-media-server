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

using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.OpticalMedia.Drives.Backends;

/// <summary>
/// Windows event-driven backend using WMI <c>__InstanceModificationEvent</c>
/// against <c>Win32_LogicalDisk</c> filtered to <c>DriveType=5</c> (CD-ROM).
/// Falls back to <see cref="PollingDriveBackend"/> at construction failure.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDriveBackend : IDriveBackend
{
    private readonly Channel<DriveEvent> _events = Channel.CreateUnbounded<DriveEvent>(
        new() { SingleReader = true, SingleWriter = true }
    );
    private readonly ILogger<WindowsDriveBackend> _logger;
    private ManagementEventWatcher? _watcher;

    public WindowsDriveBackend(ILogger<WindowsDriveBackend> logger)
    {
        _logger = logger;

        WqlEventQuery query = new(
            "SELECT * FROM __InstanceModificationEvent WITHIN 2 "
                + "WHERE TargetInstance ISA 'Win32_LogicalDisk' "
                + "AND TargetInstance.DriveType = 5"
        );
        _watcher = new(query);
        _watcher.EventArrived += OnDriveChanged;
        _watcher.Start();
    }

    public IReadOnlyList<DiscDrive> GetDrives() =>
        Optical
            .GetOpticalDrives()
            .Select(state =>
            {
                OpticalDiscType type = state.HasDisc
                    ? Optical.GetDiscType(state)
                    : OpticalDiscType.None;
                return new DiscDrive(
                    state.Path,
                    state.Label,
                    state.HasDisc,
                    type,
                    state.MountPoint
                );
            })
            .ToList();

    public async IAsyncEnumerable<DriveEvent> ListenAsync(
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        while (!ct.IsCancellationRequested)
        {
            DriveEvent ev;
            try
            {
                ev = await _events.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return ev;
        }
    }

    private void OnDriveChanged(object? sender, EventArrivedEventArgs e)
    {
        try
        {
            ManagementBaseObject target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            string deviceId = (string)target["DeviceID"];
            string mount = deviceId + Path.DirectorySeparatorChar;
            string? volumeName =
                target["VolumeName"] is string name && name.Length > 0 ? name : null;
            // Win32_LogicalDisk reports a null Size while the tray is empty; a disc
            // without a volume label still has a size, so the label alone is not
            // a reliable "disc present" signal.
            bool hasDisc = target["Size"] is not null || volumeName is not null;
            string? mountPoint = hasDisc ? mount : null;

            // A Windows drive root is both the drive's identity and its mount point.
            OpticalDriveState state = new(mount, volumeName, hasDisc, mountPoint);
            OpticalDiscType type = hasDisc ? Optical.GetDiscType(state) : OpticalDiscType.None;
            DiscDrive drive = new(mount, volumeName, hasDisc, type, mountPoint);

            DriveEventType eventType = hasDisc
                ? DriveEventType.DiscInserted
                : DriveEventType.DiscEjected;

            _events.Writer.TryWrite(new(eventType, drive));
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[WindowsDriveBackend] OnDriveChanged: {Error}", ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            _watcher.EventArrived -= OnDriveChanged;
            _watcher.Stop();
            _watcher.Dispose();
            _watcher = null;
        }
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
