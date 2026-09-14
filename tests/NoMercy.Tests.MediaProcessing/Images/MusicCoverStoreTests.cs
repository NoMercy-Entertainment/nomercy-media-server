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
using System.Text;
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Images;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Images;

[Trait("Category", "Unit")]
public sealed class MusicCoverStoreTests : IDisposable
{
    private readonly string _libraryRoot = Path.Combine(
        Path.GetTempPath(),
        $"nm-musiccover-{Guid.NewGuid():N}"
    );

    public MusicCoverStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_libraryRoot, "Artist", "Album"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
            Directory.Delete(_libraryRoot, recursive: true);
    }

    [Fact]
    public void Decode_ValidDataUri_ReturnsTheBytes()
    {
        string payload = "data:image/jpeg;base64," + Convert.ToBase64String([1, 2, 3]);

        byte[]? bytes = ImageDataUri.Decode(payload, out string? error);

        bytes.Should().Equal(1, 2, 3);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("not a data uri", ImageDataUri.NotADataUri)]
    [InlineData("data:image/jpeg;base64,***", ImageDataUri.NotBase64)]
    public void Decode_RejectedPayload_SaysWhy(string payload, string expectedError)
    {
        ImageDataUri.Decode(payload, out string? error).Should().BeNull();
        error.Should().Be(expectedError);
    }

    [Fact]
    public async Task SaveToLibraryAsync_WritesTheFileInsideTheMediaFolder()
    {
        LocalStorageDriver driver = new();
        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(new LocalStorage(driver, new StoragePathGuard([], driver)));
        MusicCoverStore store = new(driver, factory.Object);
        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = _libraryRoot,
            DriverId = Ulid.NewUlid(),
        };

        bool saved = await store.SaveToLibraryAsync(
            folder,
            Path.Combine("Artist", "Album"),
            "cover.jpg",
            new MemoryStream(Encoding.UTF8.GetBytes("jpeg"))
        );

        saved.Should().BeTrue();
        File.ReadAllText(Path.Combine(_libraryRoot, "Artist", "Album", "cover.jpg"))
            .Should()
            .Be("jpeg");
    }
}
