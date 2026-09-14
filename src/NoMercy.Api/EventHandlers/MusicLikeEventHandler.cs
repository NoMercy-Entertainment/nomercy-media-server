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
using NoMercy.Api.Services.Music;
using NoMercy.Events;
using NoMercy.Events.Music;

namespace NoMercy.Api.EventHandlers;

public class MusicLikeEventHandler : EventSubscriber
{
    private readonly MusicPlaybackService _musicPlaybackService;

    public MusicLikeEventHandler(IEventBus eventBus, MusicPlaybackService musicPlaybackService)
    {
        _musicPlaybackService = musicPlaybackService;
        Track(eventBus.Subscribe<MusicItemLikedEvent>(OnMusicItemLiked));
    }

    internal Task OnMusicItemLiked(MusicItemLikedEvent @event, CancellationToken ct)
    {
        return _musicPlaybackService.ApplyItemLikeAsync(@event.UserId, @event.ItemId, @event.Liked);
    }
}
