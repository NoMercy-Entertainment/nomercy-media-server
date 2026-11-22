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

namespace NoMercy.Tests.Setup;

public sealed class ReleaseSbomWorkflowTests
{
    private static string ReadCreateRelease()
    {
        string? overridePath = Environment.GetEnvironmentVariable("NOMERCY_RELEASE_WORKFLOW");
        if (overridePath is not null)
            return File.ReadAllText(overridePath);

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Server.sln"))
        )
            directory = directory.Parent;

        Assert.NotNull(directory);
        return File.ReadAllText(
            Path.Combine(directory.FullName, ".github", "workflows", "create-release.yml")
        );
    }

    [Fact]
    public void ReleaseGeneratesACycloneDxSbomIntoTheAssetDirectory()
    {
        string workflow = ReadCreateRelease();

        Assert.Contains("CycloneDX", workflow, StringComparison.Ordinal);
        Assert.Contains("-sbom.cdx.json", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void SbomIsWrittenBeforeTheChecksumManifestSoItIsHashedAndUploaded()
    {
        string workflow = ReadCreateRelease();

        int sbom = workflow.IndexOf("-sbom.cdx.json", StringComparison.Ordinal);
        int manifest = workflow.IndexOf(
            "uses: ./.github/actions/generate-release-manifest",
            StringComparison.Ordinal
        );

        Assert.True(sbom >= 0, "no SBOM step in create-release.yml");
        Assert.True(manifest > sbom, "SBOM must be generated before the manifest step");
    }
}
