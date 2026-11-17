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

using NoMercy.Data.ServerBackups;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class BackupFilesTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();
    private readonly LocalStorageDriver _storage = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task WriteTextAtomically_ReplacesAnExistingFile_AndLeavesNoTemporaryFile()
    {
        string path = Path.Combine(_folder.Root, "state.json");
        File.WriteAllText(path, "old");

        await BackupFiles.WriteTextAtomicallyAsync(_storage, path, "new", CancellationToken.None);

        File.ReadAllText(path).Should().Be("new");
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task WriteTextAtomically_OverwritesAStaleTemporaryFile()
    {
        string path = Path.Combine(_folder.Root, "state.json");
        File.WriteAllText(path + ".tmp", "half written");

        await BackupFiles.WriteTextAtomicallyAsync(_storage, path, "new", CancellationToken.None);

        File.ReadAllText(path).Should().Be("new");
    }

    [Fact]
    public async Task CopyDurably_CopiesTheBytes_AndNeverOverwrites()
    {
        string source = Path.Combine(_folder.Root, "source.bin");
        string target = Path.Combine(_folder.Root, "target.bin");
        File.WriteAllBytes(source, [1, 2, 3]);

        await BackupFiles.CopyDurablyAsync(_storage, source, target, CancellationToken.None);
        Func<Task> again = () =>
            BackupFiles.CopyDurablyAsync(_storage, source, target, CancellationToken.None);

        File.ReadAllBytes(target).Should().Equal(1, 2, 3);
        await again.Should().ThrowAsync<IOException>();
    }

    [Theory]
    [InlineData("20261116-040000-1234567", true)]
    [InlineData("20261116-040000-1234567\n", false)]
    [InlineData("x20261116-040000-1234567", false)]
    [InlineData("20261116-040000-123456", false)]
    [InlineData("../20261116-040000-1234567", false)]
    public void BackupSetId_AcceptsOnlyAnExactName(string id, bool valid)
    {
        BackupSetId.IsValid(id).Should().Be(valid);
    }

    [Theory]
    [InlineData("config/../../escape.json")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void ResolveInside_RefusesAPathThatLeavesTheFolder(string relative)
    {
        Action resolve = () => BackupFiles.ResolveInside(_folder.Root, relative);

        resolve.Should().Throw<InvalidDataException>();
    }
}
