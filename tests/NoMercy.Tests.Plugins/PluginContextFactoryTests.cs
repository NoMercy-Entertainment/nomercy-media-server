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

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Events;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Hub;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The three analysis facades - <see cref="IPluginContext.AudioTools" />,
/// <see cref="IPluginContext.DerivedAudio" /> and
/// <see cref="IPluginContext.MusicAnalysisWriter" /> - each gated on its own
/// hook, the same rule <see cref="PluginCapabilityGuardTests" /> exercises at
/// the guard level: declaring a capability is an intention, not a grant, and
/// the factory must hand back null rather than a facade for a hook a plugin
/// never declared.
/// </summary>
public class PluginContextFactoryTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private readonly Mock<IEventBus> _eventBus = new();
    private readonly Mock<IServiceProvider> _services = new();
    private readonly Mock<IStorage> _storage = new();
    private readonly Mock<IPluginGrantStore> _grantStore = new();
    private readonly Mock<IDataProtectionProvider> _protectionProvider = new();
    private readonly Mock<IPluginLibraryQuery> _libraryQuery = new();
    private readonly Mock<IPluginLibraryWriterFactory> _libraryWriterFactory = new();
    private readonly Mock<IPluginConfiguration> _platformConfiguration = new();
    private readonly Mock<IPluginHubContextFactory> _hubContextFactory = new();
    private readonly Mock<IPluginAudioToolsFactory> _audioToolsFactory = new();
    private readonly Mock<IPluginDerivedAudio> _derivedAudio = new();
    private readonly Mock<IPluginMusicAnalysisWriterFactory> _analysisWriterFactory = new();

    public PluginContextFactoryTests()
    {
        _protectionProvider
            .Setup(p => p.CreateProtector(It.IsAny<string>()))
            .Returns(Mock.Of<IDataProtector>());
        _hubContextFactory
            .Setup(f => f.For(It.IsAny<Ulid>()))
            .Returns(Mock.Of<IPluginHubContext>());
    }

    private PluginContextFactory BuildFactory() =>
        new(
            _eventBus.Object,
            _services.Object,
            _storage.Object,
            _grantStore.Object,
            _protectionProvider.Object,
            _libraryQuery.Object,
            _libraryWriterFactory.Object,
            _platformConfiguration.Object,
            _hubContextFactory.Object,
            audioToolsFactory: _audioToolsFactory.Object,
            derivedAudio: _derivedAudio.Object,
            analysisWriterFactory: _analysisWriterFactory.Object
        );

    private IPluginContext Create(PluginCapabilities capabilities) =>
        BuildFactory().Create(PluginId, "data", NullLogger.Instance, capabilities);

    [Fact]
    public void Create_AllThreeHooksDeclared_AllThreeNonNull_FactoriesCalledWithPluginId()
    {
        IPluginAudioTools audioTools = Mock.Of<IPluginAudioTools>();
        IPluginMusicAnalysisWriter analysisWriter = Mock.Of<IPluginMusicAnalysisWriter>();
        _audioToolsFactory.Setup(f => f.CreateFor(PluginId)).Returns(audioTools);
        _analysisWriterFactory.Setup(f => f.CreateFor(PluginId)).Returns(analysisWriter);

        PluginCapabilities capabilities = new()
        {
            Hooks = ["audioTools", "derivedAudio", "musicAnalysisWrite"],
        };

        IPluginContext context = Create(capabilities);

        Assert.Same(audioTools, context.AudioTools);
        Assert.Same(_derivedAudio.Object, context.DerivedAudio);
        Assert.Same(analysisWriter, context.MusicAnalysisWriter);
        _audioToolsFactory.Verify(f => f.CreateFor(PluginId), Times.Once);
        _analysisWriterFactory.Verify(f => f.CreateFor(PluginId), Times.Once);
    }

    [Fact]
    public void Create_NoHooksDeclared_AllThreeNull_FactoriesNeverCalled()
    {
        PluginCapabilities capabilities = new() { Hooks = [] };

        IPluginContext context = Create(capabilities);

        Assert.Null(context.AudioTools);
        Assert.Null(context.DerivedAudio);
        Assert.Null(context.MusicAnalysisWriter);
        _audioToolsFactory.Verify(f => f.CreateFor(It.IsAny<Ulid>()), Times.Never);
        _analysisWriterFactory.Verify(f => f.CreateFor(It.IsAny<Ulid>()), Times.Never);
    }

    [Fact]
    public void Create_OnlyAudioToolsHookDeclared_OnlyAudioToolsNonNull()
    {
        IPluginAudioTools audioTools = Mock.Of<IPluginAudioTools>();
        _audioToolsFactory.Setup(f => f.CreateFor(PluginId)).Returns(audioTools);

        PluginCapabilities capabilities = new() { Hooks = ["audioTools"] };

        IPluginContext context = Create(capabilities);

        Assert.Same(audioTools, context.AudioTools);
        Assert.Null(context.DerivedAudio);
        Assert.Null(context.MusicAnalysisWriter);
        _analysisWriterFactory.Verify(f => f.CreateFor(It.IsAny<Ulid>()), Times.Never);
    }
}
