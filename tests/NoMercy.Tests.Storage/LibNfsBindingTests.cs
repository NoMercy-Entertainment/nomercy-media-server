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

using System.Reflection;
using NoMercy.Storage.Drivers.Nfs.Interop;

namespace NoMercy.Tests.Storage;

/// <summary>
/// The P/Invoke signatures must match libnfs's C header. A C <c>int</c> bound as a
/// C# <c>long</c> loses the sign of an error code on x64 (the callee only writes
/// EAX, which zero-extends into RAX), so a failed call reads as a large positive
/// result and every <c>rc &lt; 0</c> check silently passes.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LibNfsBindingTests
{
    [Fact]
    public void Lseek_binding_returns_int_like_nfs_lseek_in_libnfs_h()
    {
        // libnfs.h: int nfs_lseek(struct nfs_context *, struct nfsfh *, int64_t offset,
        //                         int whence, uint64_t *current_offset);
        MethodInfo? binding = typeof(LibNfs).GetMethod(
            nameof(LibNfs.Lseek),
            BindingFlags.Static | BindingFlags.NonPublic
        );

        binding.Should().NotBeNull();
        binding!.ReturnType.Should().Be(typeof(int));
        typeof(ILibNfs).GetMethod(nameof(ILibNfs.Lseek))!.ReturnType.Should().Be(typeof(int));
    }
}
