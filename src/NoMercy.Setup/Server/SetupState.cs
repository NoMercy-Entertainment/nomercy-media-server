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

namespace NoMercy.Setup.Server;

public enum SetupPhase
{
    Unauthenticated,
    Authenticating,
    Authenticated,
    Registering,
    Registered,
    CertificateAcquired,

    /// <summary>
    /// A terminal, retryable failure: registration was rejected or the certificate
    /// poll was exhausted. Ordinal sits before Complete so IsSetupRequired stays
    /// true and the setup page keeps rendering, and after Authenticated so
    /// IsAuthenticated stays true and background polling loops that stop on it exit.
    /// </summary>
    Failed,
    Complete,
}

public class SetupState
{
    private readonly object _lock = new();

    private SetupPhase _currentPhase = SetupPhase.Unauthenticated;
    private string? _errorMessage;
    private string _phaseDetail = "";
    private string? _serverUrl;
    private TaskCompletionSource _changeSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private TaskCompletionSource _setupCompletedSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public SetupPhase CurrentPhase
    {
        get
        {
            lock (_lock)
                return _currentPhase;
        }
    }

    public string? ErrorMessage
    {
        get
        {
            lock (_lock)
                return _errorMessage;
        }
    }

    public string PhaseDetail
    {
        get
        {
            lock (_lock)
                return _phaseDetail;
        }
    }

    /// <summary>
    /// The user-facing name of the current phase, from the one table in
    /// <see cref="SetupPhaseWords"/>. Sent over the wire so the page, the
    /// terminal and the launcher tray show the same words.
    /// </summary>
    public string CurrentLabel => SetupPhaseWords.Label(CurrentPhase);

    public string? ServerUrl
    {
        get
        {
            lock (_lock)
                return _serverUrl;
        }
    }

    public bool IsSetupRequired => CurrentPhase < SetupPhase.Complete;

    public bool IsAuthenticated => CurrentPhase >= SetupPhase.Authenticated;

    public Task WaitForChangeAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            signal = _changeSignal;
        }

        return signal.Task.WaitAsync(cancellationToken);
    }

    public Task WaitForSetupCompleteAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_currentPhase >= SetupPhase.Complete)
                return Task.CompletedTask;
            signal = _setupCompletedSignal;
        }

        return signal.Task.WaitAsync(cancellationToken);
    }

    public async Task WaitForPhaseAsync(
        SetupPhase targetPhase,
        CancellationToken cancellationToken = default
    )
    {
        while (true)
        {
            lock (_lock)
            {
                if (_currentPhase >= targetPhase)
                    return;
            }

            await WaitForChangeAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Waits until setup reaches ANY terminal outcome — Complete or Failed. Failed
    /// sits before Complete in the enum (so IsSetupRequired stays true and the
    /// setup page keeps rendering it), which means a plain
    /// <see cref="WaitForPhaseAsync"/>(Complete) never unblocks on a failed setup —
    /// callers that need to stop waiting once the attempt is over, one way or the
    /// other, use this instead.
    /// </summary>
    public async Task WaitForTerminalPhaseAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_currentPhase is SetupPhase.Complete or SetupPhase.Failed)
                    return;
            }

            await WaitForChangeAsync(cancellationToken);
        }
    }

    public bool TransitionTo(SetupPhase targetPhase)
    {
        lock (_lock)
        {
            if (!IsValidTransition(_currentPhase, targetPhase))
            {
                Logger.Setup(
                    $"Invalid setup transition: {_currentPhase} → {targetPhase}",
                    LogEventLevel.Warning
                );
                return false;
            }

            SetupPhase previousPhase = _currentPhase;
            _currentPhase = targetPhase;
            _errorMessage = null;
            _phaseDetail = SetupPhaseWords.Detail(targetPhase);

            Logger.Setup($"Setup phase: {previousPhase} → {targetPhase}");
            NotifyChange();

            if (targetPhase == SetupPhase.Complete)
                _setupCompletedSignal.TrySetResult();

            return true;
        }
    }

    public void SetError(string message)
    {
        lock (_lock)
        {
            _errorMessage = message;
            Logger.Setup($"Setup error in {_currentPhase}: {message}", LogEventLevel.Error);
            NotifyChange();
        }
    }

    public void ClearError()
    {
        lock (_lock)
        {
            _errorMessage = null;
            NotifyChange();
        }
    }

    public void SetPhaseDetail(string detail)
    {
        lock (_lock)
        {
            _phaseDetail = detail;
            NotifyChange();
        }
    }

    public void SetServerUrl(string url)
    {
        lock (_lock)
        {
            _serverUrl = url;
            NotifyChange();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _currentPhase = SetupPhase.Unauthenticated;
            _errorMessage = null;
            NotifyChange();
        }
    }

    private void NotifyChange()
    {
        TaskCompletionSource previous = _changeSignal;
        _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    internal static bool IsValidTransition(SetupPhase from, SetupPhase to)
    {
        return (from, to) switch
        {
            // Forward transitions
            (SetupPhase.Unauthenticated, SetupPhase.Authenticating) => true,
            (SetupPhase.Authenticating, SetupPhase.Authenticated) => true,
            (SetupPhase.Authenticated, SetupPhase.Registering) => true,
            (SetupPhase.Registering, SetupPhase.Registered) => true,
            (SetupPhase.Registered, SetupPhase.CertificateAcquired) => true,
            (SetupPhase.CertificateAcquired, SetupPhase.Complete) => true,

            // Error recovery: authenticating can fail back to unauthenticated
            (SetupPhase.Authenticating, SetupPhase.Unauthenticated) => true,
            // Registering can fail back to authenticated (retry registration)
            (SetupPhase.Registering, SetupPhase.Authenticated) => true,
            // Retry: authenticated can stay at authenticated to re-trigger registration
            (SetupPhase.Authenticated, SetupPhase.Authenticated) => true,
            // Certificate failure can go back to registered (retry cert)
            (SetupPhase.Registered, SetupPhase.Registered) => true,

            // Distinct failure: registration was rejected, or the certificate poll
            // was exhausted. BootOrchestrator.RunRegistrationAsync used to transition
            // both of these straight to Complete ("partial functionality beats no
            // functionality") so DegradedModeRecovery could keep retrying in the
            // background without IsSetupRequired getting stuck forever — but the
            // setup page checks phase before error, so it rendered a false "Setup
            // complete!" with no error, no retry, no server URL. Failed keeps
            // IsSetupRequired true (same unstuck guarantee) while the page renders
            // it as an actual failure with a retry.
            (SetupPhase.Registering, SetupPhase.Failed) => true,
            (SetupPhase.Registered, SetupPhase.Failed) => true,

            // Retry re-enters registration from a post-registration phase without
            // sending the already-signed-in user back to login.
            (SetupPhase.Registered, SetupPhase.Authenticated) => true,
            (SetupPhase.Failed, SetupPhase.Authenticated) => true,

            _ => false,
        };
    }

    public SetupPhase DetermineInitialPhase(bool hasValidToken, bool isRegistered = true)
    {
        if (hasValidToken && isRegistered)
        {
            lock (_lock)
            {
                _currentPhase = SetupPhase.Complete;
                _setupCompletedSignal.TrySetResult();
            }

            NotifyChange();
            return SetupPhase.Complete;
        }

        if (hasValidToken && !isRegistered)
        {
            lock (_lock)
            {
                _currentPhase = SetupPhase.Authenticated;
            }

            NotifyChange();
            return SetupPhase.Authenticated;
        }

        // No valid token — stay Unauthenticated (default)
        return SetupPhase.Unauthenticated;
    }
}
