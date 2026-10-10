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

using NoMercy.NmSystem.Extensions;

namespace NoMercy.Tests.Setup.Localization;

/// <summary>
/// Harvesting untranslated keys into the checked-in I18N.xml is a developer
/// convenience: it walks up from the build output to the source tree. On an
/// installed server that walk lands somewhere without the file, so every
/// untranslated string used to log
/// "LocalizationHelper: failed to record missing key '...': Could not find a part
/// of the path '/NoMercy.Api/Resources/I18N.xml'" — noise aimed at users who
/// cannot act on it (observed throughout a live v0.1.450 run).
/// </summary>
[Trait("Category", "Unit")]
public sealed class MissingKeyHarvestIsDevOnlyTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort — a leaked temp dir must not fail the suite.
            }
        }
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "nomercy_i18n_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Creates a repo-shaped tree with the checked-in resource file.</summary>
    private string CreateSourceTree(out string expectedFile)
    {
        string root = NewTempDir();
        string resourcesDir = Path.Combine(root, "src", "NoMercy.Api", "Resources");
        Directory.CreateDirectory(resourcesDir);
        expectedFile = Path.Combine(resourcesDir, "I18N.xml");
        File.WriteAllText(expectedFile, "<Entries />");
        return root;
    }

    [Fact]
    public void ResolveSourceI18NPath_ReturnsNull_WhenSourceTreeIsAbsent()
    {
        // The installed-server shape: a deep directory with no repo above it.
        string buildOutput = Path.Combine(NewTempDir(), "a", "b", "c", "d", "e");
        Directory.CreateDirectory(buildOutput);

        Assert.Null(LocalizationHelper.ResolveSourceI18NPath(buildOutput));
    }

    [Fact]
    public void ResolveSourceI18NPath_ReturnsPath_FromADebugBuildOutput()
    {
        string root = CreateSourceTree(out string expected);
        string buildOutput = Path.Combine(
            root,
            "src",
            "NoMercy.Service",
            "bin",
            "Debug",
            "net10.0"
        );
        Directory.CreateDirectory(buildOutput);

        Assert.Equal(expected, LocalizationHelper.ResolveSourceI18NPath(buildOutput));
    }

    /// <summary>
    /// A published layout carries an extra RID folder, which is exactly what a
    /// fixed-depth parent hop cannot survive.
    /// </summary>
    [Fact]
    public void ResolveSourceI18NPath_ReturnsPath_FromAPublishOutputOneLevelDeeper()
    {
        string root = CreateSourceTree(out string expected);
        string publishOutput = Path.Combine(
            root,
            "src",
            "NoMercy.Service",
            "bin",
            "Release",
            "net10.0",
            "linux-x64",
            "publish"
        );
        Directory.CreateDirectory(publishOutput);

        Assert.Equal(expected, LocalizationHelper.ResolveSourceI18NPath(publishOutput));
    }

    [Fact]
    public void ResolveSourceI18NPath_ReturnsNull_AtTheFilesystemRoot()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Null(LocalizationHelper.ResolveSourceI18NPath(root));
    }
}
