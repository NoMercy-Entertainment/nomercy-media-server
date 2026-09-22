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
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Sideload;
using NoMercy.PluginSdk.Verification;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The install route is the only way a plugin reaches the server without shell
/// access, so what it refuses matters more than what it accepts: a file the
/// loader would pick up on the next start must never survive a rejection.
/// </summary>
public class PluginInstallEndpointTests
{
    private readonly Mock<IPluginManager> _pluginManager = new();
    private readonly Mock<IStorageDriver> _storageDriver = new();

    private sealed class StubDeveloperMode(bool enabled) : IPluginDeveloperModeSource
    {
        public bool Enabled => enabled;
    }

    private PluginController BuildController(bool developerMode = true)
    {
        PluginController controller = new(
            _pluginManager.Object,
            Mock.Of<IPluginConsentService>(),
            Mock.Of<IPluginGrantStore>(),
            Mock.Of<IPluginRestartAdvisor>(),
            _storageDriver.Object,
            new StubDeveloperMode(developerMode)
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

        return controller;
    }

    private static IFormFile FileNamed(string fileName, string content = "MZ")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
        };
    }

    [Fact]
    public async Task Install_WithDeveloperModeOff_IsRefusedAndNamesTheSwitch()
    {
        PluginController controller = BuildController(developerMode: false);

        IActionResult result = await controller.Install(
            FileNamed("radio.zip", "not really a zip"),
            CancellationToken.None
        );

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        _pluginManager.Verify(
            manager =>
                manager.InstallPluginArchiveAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<bool>(),
                    It.IsAny<Guid?>()
                ),
            Times.Never,
            "a file the server will not install has no reason to reach the disk first"
        );
    }

    [Fact]
    public async Task Install_NoFile_IsRejectedWithoutTouchingTheManager()
    {
        PluginController controller = BuildController();

        IActionResult result = await controller.Install(null, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _pluginManager.Verify(
            manager =>
                manager.InstallPluginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Install_EmptyFile_IsRejected()
    {
        PluginController controller = BuildController();

        IActionResult result = await controller.Install(
            FileNamed("plugin.dll", string.Empty),
            CancellationToken.None
        );

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
    }

    [Theory]
    [InlineData("plugin.exe")]
    [InlineData("plugin")]
    [InlineData("plugin.dll.txt")]
    public async Task Install_NonAssembly_IsRejectedAndNothingIsWritten(string fileName)
    {
        PluginController controller = BuildController();

        IActionResult result = await controller.Install(
            FileNamed(fileName),
            CancellationToken.None
        );

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _storageDriver.Verify(driver => driver.CreateDirectory(It.IsAny<string>()), Times.Never);
        _pluginManager.Verify(
            manager =>
                manager.InstallPluginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Install_StagesTheUploadAndHandsThePathToTheManager()
    {
        string? staged = null;
        MemoryStream written = new();

        _storageDriver
            .Setup(driver => driver.OpenWrite(It.IsAny<string>(), true))
            .Callback<string, bool>((path, _) => staged = path)
            .Returns(written);

        PluginController controller = BuildController();

        IActionResult result = await controller.Install(
            FileNamed("Sample.Plugin.dll", "assembly-bytes"),
            CancellationToken.None
        );

        result.Should().BeOfType<OkObjectResult>();
        staged.Should().NotBeNull();
        Path.GetFileName(staged).Should().Be("Sample.Plugin.dll");
        Encoding.UTF8.GetString(written.ToArray()).Should().Be("assembly-bytes");

        _pluginManager.Verify(
            manager => manager.InstallPluginAsync(staged!, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    /// <summary>
    /// An archive is a different install from a bare assembly, not the same one
    /// with a flag: it carries the manifest and every file the plugin ships
    /// with, so it must reach the archive path rather than the assembly path.
    /// </summary>
    [Fact]
    public async Task Install_Archive_GoesToTheArchiveInstall()
    {
        string? staged = null;

        _storageDriver
            .Setup(driver => driver.OpenWrite(It.IsAny<string>(), true))
            .Callback<string, bool>((path, _) => staged = path)
            .Returns(new MemoryStream());

        _pluginManager
            .Setup(manager =>
                manager.InstallPluginArchiveAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        PluginController controller = BuildController();

        IActionResult result = await controller.Install(
            FileNamed("Sample.Plugin.zip", "archive-bytes"),
            CancellationToken.None
        );

        result.Should().BeOfType<OkObjectResult>();
        Path.GetFileName(staged).Should().Be("Sample.Plugin.zip");

        _pluginManager.Verify(
            manager =>
                manager.InstallPluginArchiveAsync(staged!, null, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _pluginManager.Verify(
            manager =>
                manager.InstallPluginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    /// <summary>
    /// The client names the upload, so the name is an input. A name carrying a
    /// path would otherwise pick the directory the staging write lands in.
    /// <para>
    /// Both separators, on every host. The first version of this asserted the
    /// backslash form only and passed on Windows while failing on the Linux
    /// runner, because there a backslash is an ordinary character — which is
    /// exactly the disagreement the endpoint must not have.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(@"..\..\..\Windows\System32\evil.dll")]
    [InlineData("../../../etc/evil.dll")]
    [InlineData(@"C:\Users\someone\evil.dll")]
    [InlineData("/var/tmp/evil.dll")]
    public async Task Install_FileNameCarryingAPath_StagesUnderTheBareNameOnly(string uploadedName)
    {
        string? staged = null;

        _storageDriver
            .Setup(driver => driver.OpenWrite(It.IsAny<string>(), true))
            .Callback<string, bool>((path, _) => staged = path)
            .Returns(new MemoryStream());

        PluginController controller = BuildController();

        await controller.Install(FileNamed(uploadedName), CancellationToken.None);

        staged.Should().NotBeNull();
        staged.Should().EndWith("evil.dll");
        staged.Should().NotContain("..");
        staged.Should().NotContain("System32");
        staged.Should().NotContain("etc");
    }

    [Fact]
    public async Task Install_VerificationFails_IsRejectedAndTheStagingDirectoryIsRemoved()
    {
        _storageDriver
            .Setup(driver => driver.OpenWrite(It.IsAny<string>(), true))
            .Returns(new MemoryStream());
        _storageDriver.Setup(driver => driver.DirectoryExists(It.IsAny<string>())).Returns(true);

        _pluginManager
            .Setup(manager =>
                manager.InstallPluginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new PluginVerificationException("checksum mismatch"));

        PluginController controller = BuildController();

        IActionResult result = await controller.Install(
            FileNamed("Sample.Plugin.dll"),
            CancellationToken.None
        );

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _storageDriver.Verify(
            driver => driver.DeleteDirectory(It.IsAny<string>(), true),
            Times.Once
        );
    }

    [Fact]
    public async Task Install_Succeeds_TheStagingDirectoryIsStillRemoved()
    {
        _storageDriver
            .Setup(driver => driver.OpenWrite(It.IsAny<string>(), true))
            .Returns(new MemoryStream());
        _storageDriver.Setup(driver => driver.DirectoryExists(It.IsAny<string>())).Returns(true);

        PluginController controller = BuildController();

        await controller.Install(FileNamed("Sample.Plugin.dll"), CancellationToken.None);

        _storageDriver.Verify(
            driver => driver.DeleteDirectory(It.IsAny<string>(), true),
            Times.Once
        );
    }
}
