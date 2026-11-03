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
using NoMercy.Setup.Boot;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Boot;

/// <summary>
/// Requirement: <see cref="Start.InitEssential"/> is called defensively from both
/// <c>ServerBootstrapper</c> and <c>BootOrchestrator</c> ("a shim until Task 17
/// inlines task definitions"). A second call must be a no-op — it used to rebuild
/// <c>Start._allTasks</c> from scratch (discarding the Phase 2/3 task closures
/// <see cref="Start.InitRemaining"/> later reads) and re-run CreateAppFolders +
/// UserSettings a second time on every single boot for no reason.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class StartInitEssentialIdempotencyTests : IDisposable
{
    private readonly string? _originalAppPath;
    private readonly string _tempAppPath;

    public StartInitEssentialIdempotencyTests()
    {
        _originalAppPath = Environment.GetEnvironmentVariable("NOMERCY_APP_PATH");
        _tempAppPath = Path.Combine(Path.GetTempPath(), $"nm-initessential-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempAppPath);
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _tempAppPath);

        ResetStaticState();
    }

    public void Dispose()
    {
        ResetStaticState();
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _originalAppPath);
        try
        {
            if (Directory.Exists(_tempAppPath))
                Directory.Delete(_tempAppPath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Start._allTasks / _phase1Completed / _essentialInitialized are process-wide
    // statics with no public reset — every test in this class must start from the
    // same "never initialized" state the real process boots into, or an earlier
    // test's InitEssential() call would make a later test's "did it skip?" assertion
    // meaningless.
    private static void ResetStaticState()
    {
        Type startType = typeof(Start);
        SetStaticField(startType, "_allTasks", new List<StartupTask>());
        SetStaticField(startType, "_phase1Completed", new HashSet<string>());
        SetStaticField(startType, "_essentialInitialized", false);
    }

    private static void SetStaticField(Type type, string name, object value)
    {
        FieldInfo field =
            type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{type.Name} has no static field '{name}'");
        field.SetValue(null, value);
    }

    private static object GetStaticField(Type type, string name)
    {
        FieldInfo field =
            type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{type.Name} has no static field '{name}'");
        return field.GetValue(null)!;
    }

    [Fact]
    public async Task InitEssential_CalledTwice_SecondCallDoesNotRebuildTaskList()
    {
        await Start.InitEssential();
        object firstAllTasks = GetStaticField(typeof(Start), "_allTasks");
        object firstPhase1Completed = GetStaticField(typeof(Start), "_phase1Completed");

        await Start.InitEssential();
        object secondAllTasks = GetStaticField(typeof(Start), "_allTasks");
        object secondPhase1Completed = GetStaticField(typeof(Start), "_phase1Completed");

        // Reference equality proves BuildStartupTasks() (and the Phase 1 task run
        // that populates _phase1Completed) did NOT execute a second time — a rebuild
        // would have produced a brand-new List/HashSet instance.
        Assert.Same(firstAllTasks, secondAllTasks);
        Assert.Same(firstPhase1Completed, secondPhase1Completed);
    }

    [Fact]
    public async Task InitEssential_FirstCall_PopulatesTaskListAndPhase1Completion()
    {
        await Start.InitEssential();

        List<StartupTask> allTasks = (List<StartupTask>)GetStaticField(typeof(Start), "_allTasks");
        HashSet<string> phase1Completed =
            (HashSet<string>)GetStaticField(typeof(Start), "_phase1Completed");

        Assert.NotEmpty(allTasks);
        Assert.Contains("CreateAppFolders", phase1Completed);
    }
}
