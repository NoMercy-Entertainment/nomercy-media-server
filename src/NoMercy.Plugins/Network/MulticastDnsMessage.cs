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

using System.Buffers.Binary;
using System.Text;

namespace NoMercy.PluginSdk.Network;

/// <summary>One record read off the wire, reduced to what DNS-SD actually uses.</summary>
public sealed record MulticastDnsRecord(string Name, ushort Type, byte[] Data);

/// <summary>
/// Just enough DNS to ask "who offers this service" and to read the answer.
/// <para>
/// Hand-rolled rather than pulled in as a dependency: what a browse needs is a
/// name writer, a name reader that follows compression pointers, and four
/// record types. A library for that is a supply-chain risk in exchange for
/// code that fits on a screen.
/// </para>
/// </summary>
public static class MulticastDnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;

    private const ushort ClassIn = 1;
    private const ushort FlagResponse = 0x8400;

    /// <summary>A question: which instances offer this service type?</summary>
    public static byte[] Query(string serviceType)
    {
        List<byte> message = [];
        message.AddRange(Header(0, questions: 1, answers: 0));
        message.AddRange(Name(Qualified(serviceType)));
        message.AddRange(UInt16(TypePtr));
        message.AddRange(UInt16(ClassIn));

        return [.. message];
    }

    /// <summary>
    /// An unsolicited answer: this instance, at this port, with these
    /// attributes. A time-to-live of zero is the goodbye that tells the
    /// network the instance has gone.
    /// </summary>
    public static byte[] Announcement(
        string serviceType,
        string instance,
        int port,
        IReadOnlyDictionary<string, string>? attributes,
        string hostName,
        uint ttl
    )
    {
        string service = Qualified(serviceType);
        string full = $"{instance}.{service}";
        string target = hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? hostName
            : $"{hostName}.local";

        List<byte> message = [];
        message.AddRange(Header(FlagResponse, questions: 0, answers: 3));

        message.AddRange(Record(service, TypePtr, ttl, Name(full)));

        List<byte> srv = [];
        srv.AddRange(UInt16(0));
        srv.AddRange(UInt16(0));
        srv.AddRange(UInt16((ushort)port));
        srv.AddRange(Name(target));
        message.AddRange(Record(full, TypeSrv, ttl, srv));

        message.AddRange(Record(full, TypeTxt, ttl, Text(attributes)));

        return [.. message];
    }

    /// <summary>Every record in a response, names already un-compressed.</summary>
    public static IReadOnlyList<MulticastDnsRecord> Read(byte[] message)
    {
        List<MulticastDnsRecord> records = [];

        if (message.Length < 12)
            return records;

        ushort questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4, 2));
        int total =
            BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6, 2))
            + BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(8, 2))
            + BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10, 2));

        int at = 12;

        for (int question = 0; question < questions && at < message.Length; question++)
        {
            ReadName(message, ref at);
            at += 4;
        }

        for (int answer = 0; answer < total && at + 10 <= message.Length; answer++)
        {
            string name = ReadName(message, ref at);

            if (at + 10 > message.Length)
                break;

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(at, 2));
            int length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(at + 8, 2));
            at += 10;

            if (at + length > message.Length)
                break;

            records.Add(new(name, type, message.AsSpan(at, length).ToArray()));
            at += length;
        }

        return records;
    }

    /// <summary>The target host and port inside an SRV record.</summary>
    public static (string Host, int Port) ReadService(byte[] message, MulticastDnsRecord record)
    {
        if (record.Data.Length < 7)
            return (string.Empty, 0);

        int port = BinaryPrimitives.ReadUInt16BigEndian(record.Data.AsSpan(4, 2));

        // The target name may carry a compression pointer into the whole
        // message, so it is read against the message rather than the record.
        int at = IndexOf(message, record.Data) + 6;
        string host = at > 5 && at < message.Length ? ReadName(message, ref at) : string.Empty;

        return (host, port);
    }

    /// <summary>The key=value pairs inside a TXT record.</summary>
    public static IReadOnlyDictionary<string, string> ReadText(MulticastDnsRecord record)
    {
        Dictionary<string, string> attributes = [];
        int at = 0;

        while (at < record.Data.Length)
        {
            int length = record.Data[at++];

            if (length == 0 || at + length > record.Data.Length)
                break;

            string pair = Encoding.UTF8.GetString(record.Data, at, length);
            at += length;

            int equals = pair.IndexOf('=');
            if (equals > 0)
                attributes[pair[..equals]] = pair[(equals + 1)..];
        }

        return attributes;
    }

    /// <summary>The instance name inside a PTR record.</summary>
    public static string ReadPointer(byte[] message, MulticastDnsRecord record)
    {
        int at = IndexOf(message, record.Data);

        return at < 0 ? string.Empty : ReadName(message, ref at);
    }

    private static string Qualified(string serviceType) =>
        serviceType.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? serviceType
            : $"{serviceType}.local";

    private static byte[] Header(ushort flags, ushort questions, ushort answers)
    {
        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), questions);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), answers);

        return header;
    }

    private static List<byte> Record(string name, ushort type, uint ttl, List<byte> data)
    {
        List<byte> record = [];
        record.AddRange(Name(name));
        record.AddRange(UInt16(type));
        record.AddRange(UInt16(ClassIn));
        record.AddRange(UInt32(ttl));
        record.AddRange(UInt16((ushort)data.Count));
        record.AddRange(data);

        return record;
    }

    private static List<byte> Text(IReadOnlyDictionary<string, string>? attributes)
    {
        List<byte> text = [];

        foreach ((string key, string value) in attributes ?? new Dictionary<string, string>())
        {
            byte[] pair = Encoding.UTF8.GetBytes($"{key}={value}");

            if (pair.Length > 255)
                continue;

            text.Add((byte)pair.Length);
            text.AddRange(pair);
        }

        // A TXT record is never empty on the wire; one zero byte is the
        // standard way of saying it carries nothing.
        if (text.Count == 0)
            text.Add(0);

        return text;
    }

    private static List<byte> Name(string name)
    {
        List<byte> written = [];

        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);

            if (bytes.Length > 63)
                continue;

            written.Add((byte)bytes.Length);
            written.AddRange(bytes);
        }

        written.Add(0);

        return written;
    }

    /// <summary>
    /// Reads a name, following the compression pointers that make most mDNS
    /// packets small. The hop count is capped because a packet that points at
    /// itself is otherwise an infinite loop in the parser.
    /// </summary>
    private static string ReadName(byte[] message, ref int at)
    {
        List<string> labels = [];
        int hops = 0;
        int after = -1;

        while (at < message.Length && hops < 64)
        {
            int length = message[at];

            if (length == 0)
            {
                at++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (at + 1 >= message.Length)
                    break;

                int pointer = ((length & 0x3F) << 8) | message[at + 1];

                if (after < 0)
                    after = at + 2;

                at = pointer;
                hops++;
                continue;
            }

            at++;

            if (at + length > message.Length)
                break;

            labels.Add(Encoding.UTF8.GetString(message, at, length));
            at += length;
        }

        if (after >= 0)
            at = after;

        return string.Join('.', labels);
    }

    private static int IndexOf(byte[] message, byte[] data)
    {
        for (int at = 12; at + data.Length <= message.Length; at++)
        {
            if (message.AsSpan(at, data.Length).SequenceEqual(data))
                return at;
        }

        return -1;
    }

    private static byte[] UInt16(ushort value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);

        return bytes;
    }

    private static byte[] UInt32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);

        return bytes;
    }
}
