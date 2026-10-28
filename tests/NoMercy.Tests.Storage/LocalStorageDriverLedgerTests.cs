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

using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Storage;

/// <summary>
/// The local driver tells the <see cref="ServerWriteLedger"/> about every move and write it
/// makes, so the library file watcher can skip the events of the server's own output.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalStorageDriverLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-ledger-{Guid.NewGuid():N}"
    );
    private readonly LocalStorageDriver _driver = new();

    public LocalStorageDriverLedgerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string P(string name) => Path.Combine(_root, name);

    [Fact]
    public void MoveFile_marks_the_source_and_the_destination_as_server_written()
    {
        string source = P("Synthetic Song.flac");
        string destination = P("Synthetic Artist/Synthetic Song.flac");
        File.WriteAllText(source, "x");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        _driver.MoveFile(source, destination);

        ServerWriteLedger.Shared.IsServerWritten(source).Should().BeTrue();
        ServerWriteLedger.Shared.IsServerWritten(destination).Should().BeTrue();
    }

    [Fact]
    public void CopyFile_marks_the_destination_as_server_written()
    {
        string source = P("source.bin");
        string destination = P("copy.bin");
        File.WriteAllText(source, "x");

        _driver.CopyFile(source, destination, overwrite: false);

        ServerWriteLedger.Shared.IsServerWritten(destination).Should().BeTrue();
    }

    [Fact]
    public void OpenWrite_marks_the_file_as_server_written()
    {
        string target = P("written.bin");

        using (_driver.OpenWrite(target, overwrite: true)) { }

        ServerWriteLedger.Shared.IsServerWritten(target).Should().BeTrue();
    }

    [Fact]
    public void CreateDirectory_marks_the_folder_as_server_written()
    {
        string folder = P("Synthetic Album");

        _driver.CreateDirectory(folder);

        ServerWriteLedger.Shared.IsServerWritten(folder).Should().BeTrue();
    }

    [Fact]
    public void MoveDirectory_marks_both_ends_as_server_written()
    {
        string source = P("old album");
        string destination = P("new album");
        Directory.CreateDirectory(source);

        _driver.MoveDirectory(source, destination);

        ServerWriteLedger.Shared.IsServerWritten(Path.Combine(source, "a.flac")).Should().BeTrue();
        ServerWriteLedger
            .Shared.IsServerWritten(Path.Combine(destination, "a.flac"))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void A_path_the_server_never_touched_is_not_server_written()
    {
        ServerWriteLedger.Shared.IsServerWritten(P("untouched/other.flac")).Should().BeFalse();
    }
}
