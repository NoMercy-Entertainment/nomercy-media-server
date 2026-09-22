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

using System.Text.Json;
using NoMercy.Authorization;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Access;

namespace NoMercy.Service.Plugins;

/// <summary>
/// Who belongs to this server, and which of them have taken a seat.
/// <para>
/// Membership comes from the user list the rest of the server already keeps.
/// Seats are a small register beside the plugin configuration: first come,
/// first served, and a seat is kept once taken so the same person does not
/// lose their place to whoever opened the app next.
/// </para>
/// </summary>
public class PluginMembership(IUserCache userCache) : IPluginMembership
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string File => Path.Combine(AppFiles.PluginConfigPath, "seats.json");

    public bool IsAcceptedMember(Guid userId) =>
        userCache.Users.Any(user => user.Id == userId && (user.Allowed || user.Owner));

    public IReadOnlyList<Guid> EveryoneOn(Guid ownerId) =>
        [
            .. userCache
                .Users.Where(user => user.Allowed || user.Owner)
                .Select(user => user.Id)
                .Append(ownerId)
                .Where(id => id != Guid.Empty)
                .Distinct(),
        ];

    public int SeatsTakenFor(Ulid pluginId) =>
        Register().TryGetValue(pluginId.ToString(), out List<Guid>? seated) ? seated.Count : 0;

    private static Dictionary<string, List<Guid>> Register()
    {
        if (!System.IO.File.Exists(File))
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<Guid>>>(
                    System.IO.File.ReadAllText(File),
                    Json
                ) ?? [];
        }
        catch (JsonException)
        {
            // A register nobody can read means nobody has taken a seat, which
            // shares the plugin. The other way round would lock a household
            // out of something they paid for over a corrupt file.
            return [];
        }
    }
}
