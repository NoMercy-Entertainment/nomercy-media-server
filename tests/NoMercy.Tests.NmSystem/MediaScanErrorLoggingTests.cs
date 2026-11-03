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
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// A scan failure names the file or folder and the exception type, and a
/// canceled scan is not logged as a file failure.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MediaScanErrorLoggingTests : IDisposable
{
    private const string MusicFolder = "/music/Artist/[2000] Album";
    private const string TrackPath = MusicFolder + "/01 Track.mp3";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm_scan_error_log_" + Guid.NewGuid().ToString("N")
    );

    private readonly List<string> _messages = [];
    private readonly object _lock = new();

    public MediaScanErrorLoggingTests()
    {
        Directory.CreateDirectory(_root);
        Logger.LogEmitted += OnLog;
    }

    private void OnLog(LogEntry entry)
    {
        lock (_lock)
            _messages.Add(entry.Message);
    }

    private string[] Messages()
    {
        lock (_lock)
            return [.. _messages];
    }

    [Fact]
    public async Task A_canceled_probe_is_not_swallowed()
    {
        await using MediaScan mediaScan = new(new ThrowingDriver());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = async () => await mediaScan.ProbeAsync(TrackPath, true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_tag_failure_logs_the_file_and_the_exception_type()
    {
        string file = Path.Combine(_root, "01 Track.mp3");
        File.WriteAllBytes(file, [0x00, 0x01, 0x02, 0x03]);
        await using MediaScan mediaScan = new(new LocalStorageDriver());

        await mediaScan.ProbeAsync(file, true, CancellationToken.None);

        Messages()
            .Should()
            .Contain(m => m.Contains(file) && m.Contains("Exception") && m.Contains("Probe of"));
    }

    [Fact]
    public async Task A_folder_listing_failure_is_logged_with_the_folder()
    {
        await using MediaScan mediaScan = new(new ThrowingDriver());

        await mediaScan.EnableFileListing().DisableRegexFilter().Process(MusicFolder, 1);

        Messages()
            .Should()
            .Contain(m =>
                m.Contains(MusicFolder) && m.Contains("IOException") && m.Contains("list folders")
            );
    }

    public void Dispose()
    {
        Logger.LogEmitted -= OnLog;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class ThrowingDriver : IStorageDriver
    {
        public bool FileExists(string path) => true;

        public bool DirectoryExists(string path) => true;

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void DeleteFile(string path) => throw new NotSupportedException();

        public void DeleteDirectory(string path, bool recursive) =>
            throw new NotSupportedException();

        public long GetFileSize(string path) => 0;

        public DateTime GetLastWriteTimeUtc(string path) => DateTime.UtcNow;

        public DateTime GetCreationTimeUtc(string path) => DateTime.UtcNow;

        public DateTime GetLastAccessTimeUtc(string path) => DateTime.UtcNow;

        public Stream OpenRead(string path) => throw new IOException("share offline");

        public Stream OpenWrite(string path, bool overwrite) => throw new NotSupportedException();

        public void MoveFile(string source, string destination) =>
            throw new NotSupportedException();

        public void CopyFile(string source, string destination, bool overwrite) =>
            throw new NotSupportedException();

        public IEnumerable<string> EnumerateFileSystemEntries(
            string directory,
            string searchPattern,
            SearchOption option
        ) => throw new IOException("share offline");

        public string GetFullPath(string path) => path;

        public string? ResolveLinkTarget(string path) => null;

        public bool IsHidden(string path) => false;

        public void MoveDirectory(string source, string destination) =>
            throw new NotSupportedException();
    }
}
