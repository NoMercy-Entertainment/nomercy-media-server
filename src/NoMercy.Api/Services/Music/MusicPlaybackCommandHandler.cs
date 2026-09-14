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

using System.Globalization;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Services.Music;

public class MusicPlaybackCommandHandler(MusicPlaybackService musicPlaybackService)
{
    private readonly string[] _repeatStates = ["off", "one", "all"];

    public void HandleCommand(User user, string command, object? data, MusicPlayerState state)
    {
        switch (command.ToLower())
        {
            case "play":
                if (state.Actions.Disallows.Resuming)
                    break;
                HandlePlay(user, state);
                break;
            case "pause":
                if (state.Actions.Disallows.Pausing)
                    break;
                HandlePause(user, state);
                break;
            case "seek":
                if (state.Actions.Disallows.Seeking)
                    break;
                HandleSeek(state, data);
                break;
            case "next":
                if (state.Actions.Disallows.Next)
                    break;
                HandleNext(user, state);
                break;
            case "previous":
                if (state.Actions.Disallows.Previous)
                    break;
                HandlePrevious(user, state);
                break;
            case "stop":
                // stop() is unconditional per the NoMercy Connect protocol — the
                // Disallows.Stopping flag is a client UI hint, not server-side
                // enforcement (matches VideoPlaybackCommandHandler).
                HandleStop(state);
                break;
            case "mute":
                if (state.Actions.Disallows.Muting)
                    break;
                state.Muted = !state.Muted;
                break;
            case "shuffle":
                if (state.Actions.Disallows.TogglingShuffle)
                    break;
                state.Shuffle = !state.Shuffle;
                break;
            case "repeat":
                if (state.Actions.Disallows.TogglingRepeatContext)
                    break;
                HandleRepeat(state);
                break;
        }
    }

    private void HandlePlay(User user, MusicPlayerState state)
    {
        state.PlayState = true;
        musicPlaybackService.StartPlaybackTimer(user);
    }

    private void HandlePause(User user, MusicPlayerState state)
    {
        state.PlayState = false;
        musicPlaybackService.RemoveTimer(user.Id);
    }

    private void HandleSeek(MusicPlayerState state, object? data)
    {
        string raw = data?.ToString() ?? "0";
        int seekSeconds;

        if (int.TryParse(raw, out int intValue))
        {
            seekSeconds = intValue;
        }
        else if (
            double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double floatValue
            )
        )
        {
            seekSeconds = (int)floatValue;
        }
        else
        {
            seekSeconds = 0;
        }

        state.SetPosition(seekSeconds * 1000);
        state.CrossfadeSignalSent = false; // User seeked, invalidate any pending crossfade
    }

    private void HandleNext(User user, MusicPlayerState state)
    {
        if (state.CurrentItem == null)
            return;
        musicPlaybackService.RemoveTimer(user.Id);
        state.CrossfadeSignalSent = false; // Reset for new track

        // Add current item to backlog
        state.Backlog.Add(state.CurrentItem);

        // Move to the next track
        if (state.Playlist.Count > 0)
        {
            state.CurrentItem = state.Playlist.First();
            state.Playlist.RemoveAt(0);
            state.SetPosition(0);
            state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
        }
        else
        {
            HandlePlaylistCompletion(user, state);
            return;
        }

        state.PlayState = true;
        musicPlaybackService.StartPlaybackTimer(user);
    }

    private void HandlePlaylistCompletion(User user, MusicPlayerState state)
    {
        switch (state.Repeat)
        {
            case "one":
                // If repeat one, play the same item again
                state.SetPosition(0);
                state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
                musicPlaybackService.StartPlaybackTimer(user);
                break;
            case "all":
                if (state.WrapBacklogIntoPlaylist())
                {
                    state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
                    musicPlaybackService.StartPlaybackTimer(user);
                }

                break;
            default:
                state.Stop();
                break;
        }
    }

    private void HandlePrevious(User user, MusicPlayerState state)
    {
        if (state.CurrentItem == null)
            return;
        state.CrossfadeSignalSent = false; // Reset for new/restarted track

        // If we're more than 3 seconds into the song, restart it
        if (state.Time > 3000)
        {
            state.SetPosition(0);
            state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
            return;
        }

        // If within 3 seconds (or at start), go to previous track from backlog
        // If backlog is empty, just restart the current track
        if (state.Backlog.Count == 0)
        {
            state.SetPosition(0);
            state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
            return;
        }

        musicPlaybackService.RemoveTimer(user.Id);

        // Move current item back to the start of playlist
        state.Playlist.Insert(0, state.CurrentItem);

        // Move last backlog item to current
        state.CurrentItem = state.Backlog.Last();
        state.Backlog.RemoveAt(state.Backlog.Count - 1);
        state.SetPosition(0);
        state.IgnoreCurrentTimeUntil = DateTime.UtcNow.AddSeconds(1);
        state.PlayState = true;
        musicPlaybackService.StartPlaybackTimer(user);
    }

    private void HandleRepeat(MusicPlayerState state)
    {
        int currentIndex = Array.IndexOf(_repeatStates, state.Repeat);
        state.Repeat = _repeatStates[(currentIndex + 1) % _repeatStates.Length];
    }

    private void HandleStop(MusicPlayerState state)
    {
        // Do NOT clear state.DeviceId here. The active device remains the
        // active device until it actually disconnects from the WebSocket —
        // clearing it on stop made every subsequent StartPlaybackCommand
        // from a passive sender (phone tapping a playlist) route through
        // HandleNewPlayerState, which unconditionally promoted the caller
        // to active and audibly hijacked playback away from the TV.
        state.CurrentItem = null;
        state.PlayState = false;
        state.SetPosition(0);
        state.Backlog = [];
        state.Playlist = [];
        state.CurrentList = new("", UriKind.Relative);
        state.Actions = new()
        {
            Disallows = new()
            {
                Previous = true,
                Next = true,
                Resuming = true,
                Pausing = true,
                Seeking = true,
                Stopping = true,
                Muting = true,
                TogglingShuffle = true,
                TogglingRepeatContext = true,
                TogglingRepeatTrack = true,
            },
        };
    }
}
