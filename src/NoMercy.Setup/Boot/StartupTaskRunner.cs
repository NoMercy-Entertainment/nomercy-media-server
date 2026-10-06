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

using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Setup.Boot;

public class StartupTaskRunner
{
    private readonly List<StartupTask> _tasks;
    private readonly HashSet<string> _completedTasks = [];
    private readonly List<StartupTask> _deferredTasks = [];
    private readonly Dictionary<string, string> _deferredReasons = [];

    public IReadOnlyList<StartupTask> DeferredTasks => _deferredTasks;
    public IReadOnlyDictionary<string, string> DeferredReasons => _deferredReasons;
    public IReadOnlySet<string> CompletedTasks => _completedTasks;

    public StartupTaskRunner(List<StartupTask> tasks)
    {
        _tasks = tasks;
        ValidateDependencies();
    }

    public StartupTaskRunner(List<StartupTask> tasks, IEnumerable<string> alreadyCompleted)
    {
        _tasks = tasks;
        foreach (string name in alreadyCompleted)
            _completedTasks.Add(name);
        ValidateDependencies();
    }

    private void ValidateDependencies()
    {
        HashSet<string> taskNames = _tasks.Select(t => t.Name).ToHashSet();
        taskNames.UnionWith(_completedTasks);

        foreach (StartupTask task in _tasks)
        {
            if (task.DependsOn is null)
                continue;

            foreach (string dep in task.DependsOn)
            {
                if (!taskNames.Contains(dep))
                {
                    throw new InvalidOperationException(
                        $"Startup task '{task.Name}' depends on '{dep}' which does not exist"
                    );
                }
            }
        }

        // Check for circular dependencies
        HashSet<string> visited = [];
        HashSet<string> inStack = [];

        foreach (StartupTask task in _tasks)
        {
            if (HasCycle(task.Name, visited, inStack))
            {
                throw new InvalidOperationException(
                    $"Circular dependency detected involving task '{task.Name}'"
                );
            }
        }
    }

    private bool HasCycle(string taskName, HashSet<string> visited, HashSet<string> inStack)
    {
        if (inStack.Contains(taskName))
            return true;
        if (visited.Contains(taskName))
            return false;

        visited.Add(taskName);
        inStack.Add(taskName);

        StartupTask? task = _tasks.FirstOrDefault(t => t.Name == taskName);
        if (task?.DependsOn is not null)
        {
            foreach (string dep in task.DependsOn)
            {
                if (HasCycle(dep, visited, inStack))
                    return true;
            }
        }

        inStack.Remove(taskName);
        return false;
    }

    public async Task RunAll()
    {
        IEnumerable<IGrouping<int, StartupTask>> phases = _tasks
            .GroupBy(t => t.Phase)
            .OrderBy(g => g.Key);

        foreach (IGrouping<int, StartupTask> phase in phases)
        {
            List<StartupTask> phaseTasks = phase.ToList();

            foreach (StartupTask task in phaseTasks)
            {
                if (!AreDependenciesMet(task))
                {
                    if (task.CanDefer)
                    {
                        Logger.Setup(
                            $"Startup task '{task.Name}' deferred — will retry in background"
                        );
                        _deferredTasks.Add(task);
                        _deferredReasons[task.Name] =
                            $"Dependencies not met: {string.Join(", ", GetUnmetDependencies(task))}";
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Required startup task '{task.Name}' cannot run — "
                            + $"dependencies not met: {string.Join(", ", GetUnmetDependencies(task))}"
                    );
                }

                try
                {
                    await task.Action.Invoke();
                    _completedTasks.Add(task.Name);
                }
                catch (Exception ex) when (task.CanDefer)
                {
                    Logger.Setup(
                        $"Startup task '{task.Name}' not ready: {ex.Message} — will retry in background"
                    );
                    _deferredTasks.Add(task);
                    _deferredReasons[task.Name] = ex.Message;
                }
                catch (Exception ex) when (!task.CanDefer)
                {
                    Logger.Setup(
                        $"Required startup task '{task.Name}' failed: {ex.Message}",
                        LogEventLevel.Fatal
                    );
                    throw;
                }
            }
        }
    }

    internal bool AreDependenciesMet(StartupTask task)
    {
        if (task.DependsOn is null)
            return true;
        return task.DependsOn.All(dep => _completedTasks.Contains(dep));
    }

    // Internal (not private): the null-DependsOn guard below is unreachable via RunAll()
    // (AreDependenciesMet already returns true for a null DependsOn, so this method is
    // only ever invoked once dependencies are known non-null) — exposed so
    // NoMercy.Tests.Setup can still lock the guard's behavior directly.
    internal IEnumerable<string> GetUnmetDependencies(StartupTask task)
    {
        if (task.DependsOn is null)
            return [];
        return task.DependsOn.Where(dep => !_completedTasks.Contains(dep));
    }
}
