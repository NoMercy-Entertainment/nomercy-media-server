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

using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin assembly is read before it is mapped into the process, and refused
/// when it is not pure IL, reaches an escape hatch (reflection, emit, interop,
/// unsafe, dynamic, process, load context) or references a server assembly.
/// Every DLL in the plugin folder is read, at every load, and an unreadable
/// file is a refusal.
/// </summary>
public class CodeScanVerificationStageTests : IDisposable
{
    private const string Echo = "NoMercy.Plugin.Samples.Echo";
    private const string Escapes = "NoMercy.Plugin.Samples.Escapes";

    private readonly string _tempDir;

    public CodeScanVerificationStageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nomercy-code-scan-" + Ulid.NewUlid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The built output of a sample plugin project, by the pattern of
    /// <c>PluginLoadContextTests.GetFailuresPluginDllPath</c>.
    /// </summary>
    internal static string SampleDllPath(string project)
    {
        string testBinDir = Path.GetDirectoryName(
            typeof(CodeScanVerificationStageTests).Assembly.Location
        )!;
        string buildConfig = Path.GetFileName(Path.GetDirectoryName(testBinDir)!);
        string repoRoot = Path.GetFullPath(
            Path.Combine([testBinDir, "..", "..", "..", "..", ".."])
        );

        string dllPath = Path.Combine([
            repoRoot,
            "tests",
            project,
            "bin",
            buildConfig,
            "net10.0",
            $"{project}.dll",
        ]);

        if (!File.Exists(dllPath))
            throw new FileNotFoundException(
                $"Sample plugin DLL not found at '{dllPath}'. Build {project} first."
            );

        return dllPath;
    }

    /// <summary>Copies one sample DLL, alone, into a fresh folder and returns its path.</summary>
    internal static string StageAlone(string tempDir, string project)
    {
        string folder = Path.Combine(tempDir, project);
        Directory.CreateDirectory(folder);
        string dll = Path.Combine(folder, $"{project}.dll");
        File.Copy(SampleDllPath(project), dll, overwrite: true);
        return dll;
    }

    private static PluginManifest Manifest(string project) =>
        new()
        {
            Id = new PluginId(Ulid.NewUlid()),
            Name = project,
            Description = "d",
            Version = "1.0.0",
            Assembly = $"{project}.dll",
            TargetAbi = PluginAbi.Current.ToString(),
        };

    private static PluginVerificationContext Context(string assemblyPath, string? packagePath = null) =>
        new()
        {
            Manifest = Manifest(Path.GetFileNameWithoutExtension(assemblyPath)),
            AssemblyPath = assemblyPath,
            PackagePath = packagePath,
        };

    [Fact]
    public void ExposesNameAndEnforced()
    {
        CodeScanVerificationStage stage = new();

        stage.Name.Should().Be("CodeScan");
        stage.Enforced.Should().BeTrue();
    }

    /// <summary>
    /// The guard that the ban list does not catch the SDK, the BCL core, or
    /// the helpers the compiler emits for safe C# (Echo carries
    /// <c>SafeConstructs</c>: collection expressions, params spans, stackalloc
    /// into a Span, an indexer, async).
    /// </summary>
    [Fact]
    public void ACleanPluginPasses()
    {
        string dll = StageAlone(_tempDir, Echo);

        (PluginStageOutcome outcome, string? message) = new CodeScanVerificationStage().Evaluate(
            Context(dll)
        );

        message.Should().BeNull();
        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    [Fact]
    public void EveryEscapeIsNamed()
    {
        string dll = StageAlone(_tempDir, Escapes);

        (PluginStageOutcome outcome, string? message) = new CodeScanVerificationStage().Evaluate(
            Context(dll)
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().StartWith(PluginRefusalCode.CodeScan);
        message.Should().Contain("System.Type::GetType");
        message.Should().Contain("System.Reflection.Emit");
        message.Should().Contain("System.Runtime.InteropServices");
        message.Should().Match(m => m.Contains("localloc") || m.Contains("Pointer"));
        message.Should().Contain("Microsoft.CSharp.RuntimeBinder");
        message.Should().Contain("System.Diagnostics.Process");
        message.Should().Contain("System.Runtime.Loader");
    }

    [Fact]
    public void ANativeDllInTheFolderFails()
    {
        string dll = StageAlone(_tempDir, Echo);
        byte[] junk = new byte[64];
        Random.Shared.NextBytes(junk);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(dll)!, "helper.dll"), junk);

        (PluginStageOutcome outcome, string? message) = new CodeScanVerificationStage().Evaluate(
            Context(dll)
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("helper.dll");
        message.Should().Contain("not pure IL");
    }

    [Fact]
    public void ABundledServerAssemblyFails()
    {
        string dll = StageAlone(_tempDir, Escapes);
        File.Copy(
            typeof(PluginVerifier).Assembly.Location,
            Path.Combine(Path.GetDirectoryName(dll)!, "NoMercy.Plugins.dll")
        );

        (PluginStageOutcome outcome, string? message) = new CodeScanVerificationStage().Evaluate(
            Context(dll)
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("references server assembly NoMercy.Plugins");
    }

    [Fact]
    public void TheDefaultVerifierRunsTheScan()
    {
        string dll = StageAlone(_tempDir, Escapes);

        PluginVerificationResult result = new PluginVerifier().Verify(Manifest(Escapes), dll, null);

        result.Verified.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains(PluginRefusalCode.CodeScan));
    }

    /// <summary>
    /// The archive install verifies before a byte is unpacked, with the
    /// assembly path pointing inside the zip; the scan reads the entries.
    /// </summary>
    [Fact]
    public void APackageIsScannedInsideTheArchive()
    {
        string zip = Path.Combine(_tempDir, "escapes.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(SampleDllPath(Escapes), $"{Escapes}.dll");

        (PluginStageOutcome outcome, string? message) = new CodeScanVerificationStage().Evaluate(
            Context(Path.Combine(zip, $"{Escapes}.dll"), zip)
        );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("System.Runtime.Loader");
    }

    /// <summary>
    /// The bare-assembly load (install of a .dll, boot scan, enable, reload)
    /// never went through the verifier. It must scan too: every load, one line.
    /// </summary>
    [Fact]
    public async Task TheBareAssemblyLoadPathRunsTheScan()
    {
        string dll = StageAlone(_tempDir, Escapes);
        InMemoryEventBus eventBus = new();
        PluginRegistry registry = new();
        List<PluginErrorOccurredEvent> errors = [];
        eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );
        PluginLoader loader = new(
            eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir),
            registry,
            new PluginVerifier([]),
            new PluginConsentService(new InMemoryConsentStore()),
            TestPluginPlatform.ContextFactory(eventBus, TestStorageHelper.CreateStorage(_tempDir))
        );

        await loader.LoadPluginAssemblyAsync(dll);

        errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain(PluginRefusalCode.CodeScan);
        registry.Values.Should().BeEmpty();
        Directory
            .GetDirectories(_tempDir, "*", SearchOption.AllDirectories)
            .Where(d => Path.GetFileName(d) == ".loaded")
            .SelectMany(d => Directory.GetDirectories(d))
            .Should()
            .BeEmpty("a refused shadow copy is removed");
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
