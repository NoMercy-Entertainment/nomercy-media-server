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

using Newtonsoft.Json.Linq;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// Covers what a music encode job writes into its payload, and what it can still
/// read out of one.
/// <para>
/// Both directions matter, and for different reasons. Writing the release into
/// every track's payload is what took the queue database to 23.6GB and eventually
/// broke the dashboard's own queue poll. Still being able to read one back is what
/// keeps an already-queued backlog runnable across the change — a job that cannot
/// find its release does nothing, so refusing to read the old shape would quietly
/// discard every encode queued before this.
/// </para>
/// </summary>
[Trait("Category", "Jobs")]
public class MusicEncodeJobPayloadShapeTests
{
    private static readonly Guid ReleaseId = new("ec7f45e9-7935-45fb-83f0-5825c0181a2b");
    private static readonly Guid TrackId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void SerializedJob_CarriesTheReleaseId_NotTheRelease()
    {
        MusicEncodeJob job = new()
        {
            ReleaseId = ReleaseId,
            TrackId = TrackId,
            BasePath = "Music/Some Artist/Some Album",
            InputFile = "Download/complete/Album/01 Track.flac",
        };

        JObject payload = JObject.Parse(SerializationHelper.Serialize(job));

        payload["folderMetaData"]
            .Should()
            .BeNull("the release graph is what made these payloads a megabyte each");
        payload["foundTrack"].Should().BeNull();
        payload["mediaFile"].Should().BeNull();

        Guid.Parse(payload.Value<string>("releaseId")!).Should().Be(ReleaseId);
        Guid.Parse(payload.Value<string>("trackId")!).Should().Be(TrackId);
    }

    [Fact]
    public void JobQueuedBeforeTheChange_StillCarriesItsRelease_SoItCanStillRun()
    {
        string legacyPayload = $$"""
            {
              "$type": "NoMercy.MediaProcessing.Jobs.MediaJobs.MusicEncodeJob, NoMercy.MediaProcessing",
              "queueName": "encoder",
              "priority": 5,
              "id": "{{ReleaseId}}",
              "inputFile": "Download/complete/Album/01 Track.flac",
              "foundTrack": { "id": "{{TrackId}}", "title": "Track", "position": 1 },
              "folderMetaData": {
                "basePath": "Music/Some Artist/Some Album",
                "musicBrainzRelease": { "id": "{{ReleaseId}}", "title": "Some Album" }
              }
            }
            """;

        MusicEncodeJob job = SerializationHelper.Deserialize<MusicEncodeJob>(legacyPayload);

        job.Should().NotBeNull();
        job.FolderMetaData.Should()
            .NotBeNull("a row queued before this change has to keep running from what it carries");
        job.FolderMetaData.MusicBrainzRelease.Id.Should().Be(ReleaseId);
        job.FoundTrack.Id.Should().Be(TrackId);
    }

}
