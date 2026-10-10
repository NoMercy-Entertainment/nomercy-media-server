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

namespace NoMercy.Tests.Storage;

/// <summary>
/// A scope-relative folder key handed to a raw driver resolves against the process
/// working directory, so the scan finds nothing and the caller reports an empty folder
/// instead of an error. That silent zero has now cost the local-library rescan path and
/// the dashboard music file list the same day of debugging, which is why the rule lives
/// in one place and is pinned here.
/// </summary>
[Trait("Category", "Unit")]
public class StorageScopeExtensionsTests
{
    private const string ScopeRelative = "Download/complete/Blondie - Greatest Hits (2002)";

    [Fact]
    public void A_local_scope_resolves_through_the_facade_which_knows_the_library_root()
    {
        Mock<IStorage> storage = new();
        storage.Setup(s => s.GetFullPath(ScopeRelative)).Returns($"M:/{ScopeRelative}");

        storage.Object.ResolveBackendPath(ScopeRelative).Should().Be($"M:/{ScopeRelative}");

        storage.Verify(s => s.GetFullPath(ScopeRelative), Times.Once);
    }

    /// <summary>
    /// Remote backends carry their own export/bucket root inside the driver and reject the
    /// facade call, so they are the one case that resolves through the driver.
    /// </summary>
    [Fact]
    public void A_backend_that_rejects_the_facade_falls_back_to_its_driver()
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.GetFullPath(ScopeRelative))
            .Returns($"/mnt/vault/Media/{ScopeRelative}");

        Mock<IStorage> storage = new();
        storage.Setup(s => s.GetFullPath(ScopeRelative)).Throws<NotSupportedException>();
        storage.SetupGet(s => s.Driver).Returns(driver.Object);

        storage
            .Object.ResolveBackendPath(ScopeRelative)
            .Should()
            .Be($"/mnt/vault/Media/{ScopeRelative}");
    }

    /// <summary>
    /// The regression itself: the driver must never be consulted for a scope the facade
    /// can resolve, because its answer is relative to the process working directory.
    /// </summary>
    [Fact]
    public void The_driver_is_never_consulted_when_the_facade_can_answer()
    {
        Mock<IStorageDriver> driver = new();
        driver.Setup(d => d.GetFullPath(It.IsAny<string>())).Returns("/app/" + ScopeRelative);

        Mock<IStorage> storage = new();
        storage.Setup(s => s.GetFullPath(ScopeRelative)).Returns($"M:/{ScopeRelative}");
        storage.SetupGet(s => s.Driver).Returns(driver.Object);

        storage.Object.ResolveBackendPath(ScopeRelative);

        driver.Verify(d => d.GetFullPath(It.IsAny<string>()), Times.Never);
    }
}
