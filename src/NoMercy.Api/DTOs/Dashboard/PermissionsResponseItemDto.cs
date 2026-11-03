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

using Newtonsoft.Json;
using NoMercy.Database.Models.Users;

namespace NoMercy.Api.DTOs.Dashboard;

public class PermissionsResponseItemDto : User
{
    [JsonProperty("library_user")]
    public new LibraryUserDto[] LibraryUser { get; set; }

    [JsonProperty("libraries")]
    public Ulid[] Libraries { get; set; }

    public PermissionsResponseItemDto(User user)
    {
        Id = user.Id;
        Email = user.Email;
        Manage = user.Manage;
        Owner = user.Owner;
        Name = user.Name;
        Allowed = user.Allowed;
        OpticalAccess = user.OpticalAccess;
        AudioTranscoding = user.AudioTranscoding;
        VideoTranscoding = user.VideoTranscoding;
        NoTranscoding = user.NoTranscoding;
        CreatedAt = user.CreatedAt;

        LibraryUser =
        [
            .. user.LibraryUser.Select(libraryUser => new LibraryUserDto
            {
                LibraryId = libraryUser.LibraryId,
                UserId = libraryUser.UserId,
            }),
        ];

        Libraries = [.. user.LibraryUser.Select(libraryUser => libraryUser.Library.Id)];
    }
}
