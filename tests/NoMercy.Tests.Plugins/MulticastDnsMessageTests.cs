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

using System.Diagnostics;
using FluentAssertions;
using NoMercy.PluginSdk.Network;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The wire format, read back by the same code that wrote it and by hand.
/// <para>
/// Hand-written bytes matter here: a reader tested only against its own writer
/// agrees with itself about a mistake. The compression pointer is the one a
/// real router or printer will use and this code has never seen.
/// </para>
/// </summary>
public class MulticastDnsMessageTests
{
    [Fact]
    public void A_query_asks_for_the_service_type_as_a_pointer_record()
    {
        byte[] query = MulticastDnsMessage.Query("_bittorrent._tcp");

        // 12 header bytes, then the name, then two bytes of type and two of class.
        query.Should().HaveCountGreaterThan(16);
        query[4].Should().Be(0);
        query[5].Should().Be(1, "one question");
        query[^4].Should().Be(0);
        query[^3].Should().Be(12, "PTR");
        query[^2].Should().Be(0);
        query[^1].Should().Be(1, "class IN");
    }

    [Fact]
    public void A_query_qualifies_a_bare_service_type_with_local()
    {
        byte[] bare = MulticastDnsMessage.Query("_bittorrent._tcp");
        byte[] qualified = MulticastDnsMessage.Query("_bittorrent._tcp.local");

        bare.Should().Equal(qualified, "a service type with no domain is a local one");
    }

    [Fact]
    public void An_announcement_carries_the_pointer_the_service_and_the_text()
    {
        byte[] message = MulticastDnsMessage.Announcement(
            "_bittorrent._tcp",
            "nomercy",
            6881,
            new Dictionary<string, string> { ["id"] = "abc" },
            "tower",
            120
        );

        IReadOnlyList<MulticastDnsRecord> records = MulticastDnsMessage.Read(message);

        records.Should().HaveCount(3);
        records
            .Select(record => record.Type)
            .Should()
            .BeEquivalentTo([
                MulticastDnsMessage.TypePtr,
                MulticastDnsMessage.TypeSrv,
                MulticastDnsMessage.TypeTxt,
            ]);
    }

    [Fact]
    public void The_pointer_names_the_instance_inside_the_service_type()
    {
        byte[] message = Announcement();

        MulticastDnsRecord pointer = Records(message, MulticastDnsMessage.TypePtr);

        MulticastDnsMessage
            .ReadPointer(message, pointer)
            .Should()
            .Be("nomercy._bittorrent._tcp.local");
    }

    [Fact]
    public void The_service_record_carries_the_port_that_was_announced()
    {
        byte[] message = Announcement();

        (string host, int port) = MulticastDnsMessage.ReadService(
            message,
            Records(message, MulticastDnsMessage.TypeSrv)
        );

        port.Should().Be(6881);
        host.Should().Be("tower.local", "a bare host name is a local one");
    }

    [Fact]
    public void The_text_record_reads_back_the_attributes_that_went_in()
    {
        byte[] message = MulticastDnsMessage.Announcement(
            "_bittorrent._tcp",
            "nomercy",
            6881,
            new Dictionary<string, string> { ["id"] = "abc", ["v"] = "1" },
            "tower",
            120
        );

        IReadOnlyDictionary<string, string> attributes = MulticastDnsMessage.ReadText(
            Records(message, MulticastDnsMessage.TypeTxt)
        );

        attributes.Should().HaveCount(2);
        attributes["id"].Should().Be("abc");
        attributes["v"].Should().Be("1");
    }

    [Fact]
    public void An_announcement_with_no_attributes_still_carries_a_readable_text_record()
    {
        byte[] message = MulticastDnsMessage.Announcement(
            "_bittorrent._tcp",
            "nomercy",
            6881,
            null,
            "tower",
            120
        );

        MulticastDnsMessage
            .ReadText(Records(message, MulticastDnsMessage.TypeTxt))
            .Should()
            .BeEmpty("an empty TXT record is one zero byte, never zero bytes");
    }

    [Fact]
    public void A_goodbye_is_the_same_records_with_no_lifetime_left()
    {
        byte[] goodbye = MulticastDnsMessage.Announcement(
            "_bittorrent._tcp",
            "nomercy",
            0,
            null,
            "tower",
            0
        );

        MulticastDnsMessage.Read(goodbye).Should().HaveCount(3);
        MulticastDnsMessage
            .ReadPointer(goodbye, Records(goodbye, MulticastDnsMessage.TypePtr))
            .Should()
            .Be("nomercy._bittorrent._tcp.local");
    }

    /// <summary>
    /// A name written once and pointed at afterwards, which is how every real
    /// mDNS packet is built. A reader that does not follow the pointer reads
    /// an empty name and drops a service that is really there.
    /// </summary>
    [Fact]
    public void A_name_written_as_a_compression_pointer_is_followed()
    {
        List<byte> message =
        [
            // Header: no questions, one answer.
            0,
            0,
            0x84,
            0,
            0,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
        ];

        // One PTR record whose own name is written out at offset 12 and whose
        // target is a pointer back to it, which is what saves the bytes.
        message.AddRange([3, (byte)'n', (byte)'a', (byte)'s']);
        message.AddRange([5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0]);
        message.AddRange([0, 12]);
        message.AddRange([0, 1]);
        message.AddRange([0, 0, 0, 120]);
        message.AddRange([0, 2]);
        message.AddRange([0xC0, 12]);

        byte[] packet = [.. message];
        IReadOnlyList<MulticastDnsRecord> records = MulticastDnsMessage.Read(packet);

        records.Should().ContainSingle();
        records[0].Name.Should().Be("nas.local");
        MulticastDnsMessage
            .ReadPointer(packet, records[0])
            .Should()
            .Be(
                "nas.local",
                "a reader that ignores the pointer drops a service that is really there"
            );
    }

    /// <summary>
    /// A packet that points at itself. Without a hop cap the parser loops
    /// forever on one malformed datagram, and discovery is exactly where a
    /// malformed datagram arrives from a stranger's device.
    /// </summary>
    [Fact]
    public void A_pointer_that_loops_back_on_itself_still_returns()
    {
        byte[] message =
        [
            0,
            0,
            0x84,
            0,
            0,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
            // A name at offset 12 that points at offset 12.
            0xC0,
            12,
            0,
            12,
            0,
            1,
            0,
            0,
            0,
            120,
            0,
            0,
        ];

        // Timed, not merely caught: an unbounded loop here does not throw, it
        // just never ends, and "did not throw" is exactly what it looks like
        // while a thread spins on one stranger's malformed datagram.
        Stopwatch clock = Stopwatch.StartNew();
        MulticastDnsMessage.Read(message);
        clock.Stop();

        clock
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(1),
                "a packet that points at itself must stop the parser, not occupy it"
            );
    }

    [Fact]
    public void A_truncated_packet_is_read_as_far_as_it_goes_and_no_further()
    {
        byte[] message = Announcement();

        Action read = () => MulticastDnsMessage.Read(message[..(message.Length / 2)]);

        read.Should().NotThrow("a short datagram is a normal thing to receive, not a crash");
    }

    [Fact]
    public void A_packet_too_short_to_hold_a_header_answers_nothing()
    {
        MulticastDnsMessage.Read([0, 1, 2]).Should().BeEmpty();
    }

    private static byte[] Announcement() =>
        MulticastDnsMessage.Announcement(
            "_bittorrent._tcp",
            "nomercy",
            6881,
            new Dictionary<string, string> { ["id"] = "abc" },
            "tower",
            120
        );

    private static MulticastDnsRecord Records(byte[] message, ushort type) =>
        MulticastDnsMessage.Read(message).Single(record => record.Type == type);
}
