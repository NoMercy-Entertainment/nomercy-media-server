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
using Newtonsoft.Json;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// Every queued job carries ids, not provider responses.
///
/// <para>A music encode used to serialize the whole MusicBrainz release into the
/// payload of every track of the album, which took queue.db to 23.6GB and
/// eventually broke the dashboard's own queue poll on "database or disk is
/// full". Three other jobs had the same shape and were only spared by having
/// nothing queued at the time.</para>
///
/// <para>This walks the type system rather than a list, so a job added later is
/// covered without anyone remembering to add it here. A job that genuinely needs
/// bulk input has somewhere to put it — the shared-input blob store — and a job
/// that only needs to know WHICH artist or release it is about should say so with
/// an id.</para>
/// </summary>
[Trait("Category", "Queue")]
public class JobPayloadsCarryIdsNotGraphsTests
{
    private static readonly string[] ProviderNamespaces =
    [
        "NoMercy.Providers.MusicBrainz",
        "NoMercy.Providers.Tmdb",
        "NoMercy.Providers.TMDB",
        "NoMercy.Providers.Tadb",
        "NoMercy.Providers.AcoustId",
        "NoMercy.Providers.FanArt",
        "NoMercy.Providers.CoverArt",
        "NoMercy.Providers.MusixMatch",
        "NoMercy.Providers.Lrclib",
        "NoMercy.Providers.OpenSubtitles",
        "NoMercy.Providers.Tvdb",
    ];

    private static IEnumerable<Type> QueuedJobTypes()
    {
        // Loaded off disk rather than read from AppDomain: which assemblies are
        // already loaded depends on what else the run happened to touch, so the
        // same check found a different set of jobs alone than it did in a full
        // suite. The output directory is the same either way.
        string probeDirectory =
            Path.GetDirectoryName(typeof(JobPayloadsCarryIdsNotGraphsTests).Assembly.Location)
            ?? AppContext.BaseDirectory;

        Assembly[] assemblies =
        [
            .. Directory
                .EnumerateFiles(probeDirectory, "NoMercy*.dll")
                .Select(path =>
                {
                    try
                    {
                        return Assembly.LoadFrom(path);
                    }
                    catch (Exception)
                    {
                        // Native or otherwise unloadable neighbour — it holds no
                        // managed job types by definition.
                        return null;
                    }
                })
                .OfType<Assembly>(),
        ];

        return assemblies
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException loadFailure)
                {
                    return loadFailure.Types.OfType<Type>();
                }
            })
            .Where(type =>
                typeof(IShouldQueue).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false }
            );
    }

    /// <summary>
    /// Whether a property's type is (or wraps) a provider response. Checks the
    /// type itself, then unwraps arrays and generic arguments — a
    /// List&lt;MusicBrainzTrack&gt; is the same defect as a bare one.
    /// </summary>
    private static bool IsProviderType(Type type)
    {
        Type target = Nullable.GetUnderlyingType(type) ?? type;

        string ns = target.Namespace ?? string.Empty;
        if (ProviderNamespaces.Any(candidate => ns.StartsWith(candidate, StringComparison.Ordinal)))
            return true;

        if (target.IsArray)
            return IsProviderType(target.GetElementType()!);

        // Only the arguments, never the open definition — that is generic too,
        // and asking it the same question again never terminates.
        return target.IsGenericType && target.GetGenericArguments().Any(IsProviderType);
    }

    /// <summary>
    /// A property only reaches the payload if Newtonsoft would write it: not
    /// [JsonIgnore], and not opted out by a ShouldSerializeX() returning false.
    /// </summary>
    private static bool IsSerialized(Type owner, PropertyInfo property)
    {
        if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            return false;

        if (property.GetSetMethod() is null)
            return false;

        MethodInfo? optOut = owner.GetMethod(
            $"ShouldSerialize{property.Name}",
            BindingFlags.Public | BindingFlags.Instance
        );

        if (optOut is null || optOut.ReturnType != typeof(bool))
            return true;

        object? instance = TryCreate(owner);
        if (instance is null)
            return true;

        return optOut.Invoke(instance, null) is true;
    }

    private static object? TryCreate(Type type)
    {
        try
        {
            return type.GetConstructor(Type.EmptyTypes) is null
                ? null
                : Activator.CreateInstance(type);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The jobs that still carry a provider response, each one the inherited
    /// <c>Storage</c> of <c>AbstractMediaExraDataJob&lt;T&gt;</c> or
    /// <c>AbstractShowExtraDataJob&lt;T, TName&gt;</c>.
    ///
    /// <para>Not fixed with the music jobs because the shape of the fix is
    /// different: these receive their TMDB response from the dispatcher's generic
    /// <c>DispatchJob&lt;TJob, TChild&gt;(TChild data)</c>, which is synchronous
    /// and called from managers throughout the import path. Putting the response
    /// in the shared-input store means making that dispatch async, which is a
    /// change to the dispatcher's contract rather than to these six jobs.</para>
    ///
    /// <para>This list is a ratchet, not an excuse: a job that is NOT here and
    /// carries a provider response fails this test, and a job listed here that
    /// gets fixed also fails it until it is removed. It cannot silently grow.</para>
    /// </summary>
    private static readonly string[] KnownRemaining =
    [
        "CollectionExtrasJob.Storage",
        "EpisodeExtrasJob.Storage",
        "MovieExtrasJob.Storage",
        "PersonExtrasJob.Storage",
        "SeasonExtrasJob.Storage",
        "ShowExtrasJob.Storage",
    ];

    [Fact]
    public void NoQueuedJobSerializesAProviderResponseIntoItsPayload()
    {
        List<Type> jobs = [.. QueuedJobTypes()];

        jobs.Should()
            .NotBeEmpty("the reflection walk must actually find the job types it claims to cover");

        List<string> offenders = [];

        foreach (Type job in jobs)
        {
            foreach (
                PropertyInfo property in job.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance
                )
            )
            {
                if (IsProviderType(property.PropertyType) && IsSerialized(job, property))
                    offenders.Add($"{job.Name}.{property.Name}");
            }
        }

        List<string> unexpected = [.. offenders.Except(KnownRemaining).OrderBy(name => name)];
        List<string> nowFixed = [.. KnownRemaining.Except(offenders).OrderBy(name => name)];

        unexpected
            .Should()
            .BeEmpty(
                "a queued job must carry the id of what it is about, not the provider "
                    + "response describing it. {0} job types checked, {1} known-remaining",
                jobs.Count,
                KnownRemaining.Length
            );

        nowFixed
            .Should()
            .BeEmpty(
                "these no longer carry a provider response — take them off "
                    + nameof(KnownRemaining)
                    + " so the ratchet keeps holding them fixed"
            );
    }
}
