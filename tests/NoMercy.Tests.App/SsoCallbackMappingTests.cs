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

using System.Runtime.CompilerServices;
using Xunit;

namespace NoMercy.Tests.App;

public sealed class SsoCallbackMappingTests
{
    [Fact]
    public void DesktopApp_DoesNotMapUnpreparedSsoCallback()
    {
        string source = File.ReadAllText(AppProgramSource());

        Assert.DoesNotContain("\"/sso-callback\"", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string AppProgramSource([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(testFile)!,
                "..",
                "..",
                "src",
                "NoMercy.App",
                "Program.cs"
            )
        );
}
