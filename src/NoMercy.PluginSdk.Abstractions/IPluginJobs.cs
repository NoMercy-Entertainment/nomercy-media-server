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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What became of a job this plugin asked for.
///
/// <para>
/// A plugin that owns files has to know. The torrent downloader stages a copy
/// into an intake folder and keeps the download seeding, and both may only be
/// deleted once the encode has landed: too early loses the episode, never costs
/// the owner two extra copies of everything, re-checked on every start, for
/// ever - which is the state one owner's disk was actually found in.
/// </para>
///
/// <para>
/// With nothing to read, a plugin infers the outcome from the library, and that
/// is wrong in two ways it cannot detect. An encode that failed looks exactly
/// like one still running - both are "the library does not have it yet", for
/// ever - so the owner waits for an episode that will never arrive with no line
/// anywhere saying why. And a file that arrived some other way looks like
/// success, so the plugin deletes its download believing its own encode
/// finished.
/// </para>
/// </summary>
public interface IPluginJobs
{
    /// <summary>
    /// What became of one job.
    /// </summary>
    /// <remarks>
    /// Only jobs this plugin dispatched are visible. A plugin has no business
    /// reading the whole queue, and the server does not offer it.
    /// </remarks>
    Task<PluginJobStatus?> StatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Queues one piece of work.
    /// <para>
    /// A plugin with no way to ask reached the server's own job classes by name
    /// and drove them with reflection. The class moved, the plugin kept
    /// compiling, and the work stopped happening with no line anywhere saying
    /// so. A kind is a word the host knows, so that cannot happen again.
    /// </para>
    /// <para>
    /// The parent, when given, is a job this plugin already dispatched, so the
    /// server can order them and the plugin need not wait on one to ask for the
    /// next.
    /// </para>
    /// </summary>
    Task<JobId> DispatchAsync(
        PluginJobKind kind,
        MediaId subject,
        JobId? parent = null,
        CancellationToken ct = default
    ) =>
        throw new PluginRefusedException(
            PluginRefusalMessages.CapabilityNotDeclared(
                "unknown plugin",
                PluginCapabilityNames.JobsDispatch,
                "The plugin asked the server to queue work."
            )
        );
}

/// <summary>
/// Where a job has got to.
/// </summary>
public enum PluginJobState
{
    Queued,
    Running,
    Finished,
    Failed,

    /// <summary>
    /// The server no longer has this job.
    ///
    /// <para>
    /// Deliberately not <see cref="Failed" />. A queue that was pruned is not
    /// the same as a job that went wrong, and a caller treating them alike
    /// deletes an owner's file over a tidy-up.
    /// </para>
    /// </summary>
    Unknown,
}

/// <param name="Failure">Why it failed, in words the owner can act on. Null unless <see cref="PluginJobState.Failed" />.</param>
public sealed record PluginJobStatus(
    string JobId,
    PluginJobState State,
    string? Failure,
    DateTimeOffset? FinishedAt
)
{
    /// <summary>Whether this job is done, either way. The question a caller holding a file actually has.</summary>
    public bool Settled => State is PluginJobState.Finished or PluginJobState.Failed;
}

/// <summary>
/// Raised on <see cref="IPluginContext.Events" /> when a job a plugin asked
/// for is done, so a plugin can be told rather than having to poll.
/// </summary>
public sealed record PluginJobFinished(string JobId, bool Succeeded, string? Failure);
