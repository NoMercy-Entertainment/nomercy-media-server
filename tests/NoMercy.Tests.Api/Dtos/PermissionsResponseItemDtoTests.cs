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
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api.Dtos;

/// <summary>
/// The dashboard edits a user's permissions from what this DTO reports, so
/// every stored flag has to come back as stored. optical_access was declared
/// on the user but never copied, so the optical drive switch always showed
/// as off no matter what was saved. (#249)
/// </summary>
public class PermissionsResponseItemDtoTests
{
    private static User BuildUser(bool opticalAccess)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Email = "viewer@example.com",
            Name = "Viewer",
            Allowed = true,
            OpticalAccess = opticalAccess,
        };
    }

    [Fact]
    public void A_user_with_optical_access_reports_it_on()
    {
        PermissionsResponseItemDto dto = new(BuildUser(opticalAccess: true));

        dto.OpticalAccess.Should().BeTrue("the saved permission is on");
    }

    [Fact]
    public void A_user_without_optical_access_reports_it_off()
    {
        PermissionsResponseItemDto dto = new(BuildUser(opticalAccess: false));

        dto.OpticalAccess.Should().BeFalse();
    }

    [Fact]
    public void The_serialised_item_carries_optical_access_true()
    {
        PermissionsResponseItemDto dto = new(BuildUser(opticalAccess: true));

        string json = JsonConvert.SerializeObject(dto);

        json.Should().Contain("\"optical_access\":true");
    }
}
