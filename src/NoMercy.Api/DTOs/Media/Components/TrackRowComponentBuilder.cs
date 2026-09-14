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

using NoMercy.Api.DTOs.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Specialized builder for NMTrackRow with displayList support.
/// </summary>
public class TrackRowComponentBuilder : IComponentBuilder
{
    private readonly ComponentEnvelope _envelope;
    private readonly TrackRowProps _props;

    public TrackRowComponentBuilder()
    {
        _props = new();
        _envelope = new() { Component = ComponentTypes.TrackRow, Props = _props };
    }

    public TrackRowComponentBuilder WithId(dynamic id)
    {
        _props.Id = id;
        return this;
    }

    public TrackRowComponentBuilder WithNavigation(
        dynamic? previousId = null,
        dynamic? nextId = null
    )
    {
        _props.PreviousId = previousId;
        _props.NextId = nextId;
        return this;
    }

    public TrackRowComponentBuilder WithTitle(string? title)
    {
        _props.Title = title.OrEmpty();
        return this;
    }

    public TrackRowComponentBuilder WithData(TrackRowData data)
    {
        _props.Data = data;
        return this;
    }

    public TrackRowComponentBuilder WithWatch(bool watch = true)
    {
        _props.Watch = watch;
        return this;
    }

    public TrackRowComponentBuilder WithDisplayList(IEnumerable<TrackRowData>? displayList)
    {
        _props.DisplayList = displayList;
        return this;
    }

    public TrackRowComponentBuilder WithContextMenu(IEnumerable<ContextMenuItemDto>? items)
    {
        _props.ContextMenuItems = items;
        return this;
    }

    public TrackRowComponentBuilder WithUpdate(UpdateDto update)
    {
        _envelope.Update = update;
        return this;
    }

    public TrackRowComponentBuilder WithReplacing(Ulid replacingId)
    {
        _envelope.Replacing = replacingId;
        return this;
    }

    public ComponentEnvelope Build()
    {
        return _envelope;
    }

    public static implicit operator ComponentEnvelope(TrackRowComponentBuilder builder) =>
        builder.Build();

    public TrackRowComponentBuilder WithProperties(Dictionary<string, dynamic>? properties)
    {
        _props.Properties = properties;
        return this;
    }
}
