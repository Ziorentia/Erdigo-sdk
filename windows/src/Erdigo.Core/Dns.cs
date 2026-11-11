using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Erdigo.Core;

/// <summary>DNS record types this code reads and writes.</summary>
internal enum DnsType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

internal sealed record DnsQuestion(string Name, DnsType Type, bool UnicastResponse = false);

/// <summary>One resource record. Only the fields of the types above are kept.</summary>
internal sealed record DnsRecord(string Name, DnsType Type, uint Ttl, bool CacheFlush)
{
    /// <summary>For PTR and SRV: the name pointed to.</summary>
    public string? Target { get; init; }

    /// <summary>For SRV.</summary>
    public ushort Port { get; init; }

    /// <summary>For A and AAAA.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>For TXT: each string, as text.</summary>
    public IReadOnlyList<string> Text { get; init; } = Array.Empty<string>();
}

internal sealed record DnsMessage(
    ushort Id,
    bool IsResponse,
    bool Authoritative,
    IReadOnlyList<DnsQuestion> Questions,
    IReadOnlyList<DnsRecord> Answers,
    IReadOnlyList<DnsRecord> Additionals)
{
    public IEnumerable<DnsRecord> AllRecords => Answers.Concat(Additionals);
}

/// <summary>
/// A small DNS message reader and writer, for multicast DNS service discovery on the local network (RFC 6762 and
/// RFC 6763). Everything it reads comes from the network, so every length and every name pointer is checked.
/// </summary>
internal static class DnsWire
{
    public const int MdnsPort = 5353;
    public static readonly IPAddress MulticastV4 = IPAddress.Parse("224.0.0.251");
    public static readonly IPAddress MulticastV6 = IPAddress.Parse("ff02::fb");
    private const int MaxNameLength = 255;
    private const int MaxMessageLength = 9000;

    public static string ServiceName => IntegrationV1.ServiceType + ".local";

    public static bool NamesEqual(string left, string right) =>
        string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    public static byte[] Encode(DnsMessage message)
    {
        var output = new List<byte>(512);
        void U16(int value)
        {
            output.Add((byte)(value >> 8));
            output.Add((byte)value);
        }
        void U32(uint value)
        {
            U16((int)(value >> 16));
            U16((int)(value & 0xFFFF));
        }
        void Name(string name)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length is 0 or > 63)
                {
                    throw new ArgumentException("a DNS label is 1 to 63 bytes", nameof(name));
                }
                output.Add((byte)bytes.Length);
                output.AddRange(bytes);
            }
            output.Add(0);
        }
        void Record(DnsRecord record)
        {
            Name(record.Name);
            U16((int)record.Type);
            U16(0x0001 | (record.CacheFlush ? 0x8000 : 0));
            U32(record.Ttl);
            var lengthAt = output.Count;
            U16(0);
            var start = output.Count;
            switch (record.Type)
            {
                case DnsType.Ptr:
                    Name(record.Target ?? throw new ArgumentException("a PTR record needs a target"));
                    break;
                case DnsType.Srv:
                    U16(0);
                    U16(0);
                    U16(record.Port);
                    Name(record.Target ?? throw new ArgumentException("an SRV record needs a target"));
                    break;
                case DnsType.Txt:
                    foreach (var text in record.Text.DefaultIfEmpty(string.Empty))
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        output.Add((byte)Math.Min(bytes.Length, 255));
                        output.AddRange(bytes.Take(255));
                    }
                    break;
                case DnsType.A:
                case DnsType.Aaaa:
                    output.AddRange((record.Address ?? throw new ArgumentException("an address record needs an address")).GetAddressBytes());
                    break;
                default:
                    throw new ArgumentException("unsupported record type");
            }
            var length = output.Count - start;
            output[lengthAt] = (byte)(length >> 8);
            output[lengthAt + 1] = (byte)length;
        }

        U16(message.Id);
        U16((message.IsResponse ? 0x8000 : 0) | (message.Authoritative ? 0x0400 : 0));
        U16(message.Questions.Count);
        U16(message.Answers.Count);
        U16(0);
        U16(message.Additionals.Count);
        foreach (var question in message.Questions)
        {
            Name(question.Name);
            U16((int)question.Type);
            U16(0x0001 | (question.UnicastResponse ? 0x8000 : 0));
        }
        foreach (var record in message.Answers)
        {
            Record(record);
        }
        foreach (var record in message.Additionals)
        {
            Record(record);
        }
        return output.ToArray();
    }

    /// <summary>Reads a message, or returns null for anything malformed. Records of other types are skipped.</summary>
    public static DnsMessage? TryDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data.Length > MaxMessageLength)
        {
            return null;
        }
        var bytes = data.ToArray();
        try
        {
            var id = (ushort)((bytes[0] << 8) | bytes[1]);
            var flags = (bytes[2] << 8) | bytes[3];
            // Only a standard query or response with no error is of any use.
            if (((flags >> 11) & 0xF) != 0 || (flags & 0xF) != 0)
            {
                return null;
            }
            var counts = new int[4];
            for (var index = 0; index < 4; index++)
            {
                counts[index] = (bytes[4 + index * 2] << 8) | bytes[5 + index * 2];
            }
            var offset = 12;
            var questions = new List<DnsQuestion>();
            for (var index = 0; index < counts[0]; index++)
            {
                var name = ReadName(bytes, ref offset);
                Require(bytes, offset, 4);
                var type = (bytes[offset] << 8) | bytes[offset + 1];
                var klass = (bytes[offset + 2] << 8) | bytes[offset + 3];
                offset += 4;
                questions.Add(new DnsQuestion(name, (DnsType)type, (klass & 0x8000) != 0));
            }
            var sections = new List<DnsRecord>[3] { new(), new(), new() };
            for (var section = 0; section < 3; section++)
            {
                for (var index = 0; index < counts[section + 1]; index++)
                {
                    var record = ReadRecord(bytes, ref offset);
                    if (record is not null)
                    {
                        sections[section].Add(record);
                    }
                }
            }
            return new DnsMessage(id, (flags & 0x8000) != 0, (flags & 0x0400) != 0, questions, sections[0], sections[2]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Require(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > bytes.Length)
        {
            throw new FormatException("truncated DNS message");
        }
    }

    private static DnsRecord? ReadRecord(byte[] bytes, ref int offset)
    {
        var name = ReadName(bytes, ref offset);
        Require(bytes, offset, 10);
        var type = (bytes[offset] << 8) | bytes[offset + 1];
        var klass = (bytes[offset + 2] << 8) | bytes[offset + 3];
        var ttl = (uint)((bytes[offset + 4] << 24) | (bytes[offset + 5] << 16) | (bytes[offset + 6] << 8) | bytes[offset + 7]);
        var length = (bytes[offset + 8] << 8) | bytes[offset + 9];
        offset += 10;
        Require(bytes, offset, length);
        var start = offset;
        var end = offset + length;
        offset = end;
        if ((klass & 0x7FFF) != 0x0001)
        {
            return null;
        }
        var cacheFlush = (klass & 0x8000) != 0;
        switch ((DnsType)type)
        {
            case DnsType.Ptr:
            {
                var position = start;
                var target = ReadName(bytes, ref position, end);
                return new DnsRecord(name, DnsType.Ptr, ttl, cacheFlush) { Target = target };
            }
            case DnsType.Srv:
            {
                if (length < 7)
                {
                    throw new FormatException("short SRV record");
                }
                var port = (ushort)((bytes[start + 4] << 8) | bytes[start + 5]);
                var position = start + 6;
                var target = ReadName(bytes, ref position, end);
                return new DnsRecord(name, DnsType.Srv, ttl, cacheFlush) { Target = target, Port = port };
            }
            case DnsType.Txt:
            {
                var texts = new List<string>();
                var position = start;
                while (position < end)
                {
                    var size = bytes[position];
                    position += 1;
                    if (position + size > end)
                    {
                        throw new FormatException("bad TXT record");
                    }
                    texts.Add(Encoding.UTF8.GetString(bytes, position, size));
                    position += size;
                }
                return new DnsRecord(name, DnsType.Txt, ttl, cacheFlush) { Text = texts };
            }
            case DnsType.A when length == 4:
                return new DnsRecord(name, DnsType.A, ttl, cacheFlush) { Address = new IPAddress(bytes.AsSpan(start, 4)) };
            case DnsType.Aaaa when length == 16:
                return new DnsRecord(name, DnsType.Aaaa, ttl, cacheFlush) { Address = new IPAddress(bytes.AsSpan(start, 16)) };
            default:
                return null;
        }
    }

    /// <summary>
    /// Reads a possibly compressed name. A pointer must point backwards, to before where this name started, so a
    /// loop of pointers cannot exist.
    /// </summary>
    private static string ReadName(byte[] bytes, ref int offset, int limit = int.MaxValue)
    {
        var labels = new List<string>();
        var total = 0;
        var position = offset;
        var lowestStart = offset;
        var jumped = false;
        while (true)
        {
            Require(bytes, position, 1);
            if (!jumped && position >= limit)
            {
                throw new FormatException("name runs past its record");
            }
            var size = bytes[position];
            if ((size & 0xC0) == 0xC0)
            {
                Require(bytes, position, 2);
                var target = ((size & 0x3F) << 8) | bytes[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }
                if (target >= lowestStart)
                {
                    throw new FormatException("a name pointer must point backwards");
                }
                lowestStart = target;
                position = target;
                continue;
            }
            if ((size & 0xC0) != 0)
            {
                throw new FormatException("unknown label type");
            }
            position += 1;
            if (size == 0)
            {
                if (!jumped)
                {
                    offset = position;
                }
                break;
            }
            Require(bytes, position, size);
            total += size + 1;
            if (total > MaxNameLength)
            {
                throw new FormatException("name too long");
            }
            labels.Add(Encoding.UTF8.GetString(bytes, position, size));
            position += size;
        }
        if (!jumped && offset > limit)
        {
            throw new FormatException("name runs past its record");
        }
        return string.Join('.', labels);
    }
}

/// <summary>Which addresses count as the local network.</summary>
internal static class LocalNetwork
{
    /// <summary>
    /// True for this computer, link-local addresses and addresses on a subnet one of this computer's interfaces is
    /// directly on.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
        }
        foreach (var unicast in UpInterfaces().SelectMany(item => item.GetIPProperties().UnicastAddresses))
        {
            if (unicast.Address.AddressFamily != address.AddressFamily)
            {
                continue;
            }
            if (SamePrefix(unicast.Address, address, unicast.PrefixLength))
            {
                return true;
            }
        }
        return false;
    }

    public static IEnumerable<NetworkInterface> UpInterfaces()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NetworkInterface>();
        }
        return all.Where(item => item.OperationalStatus == OperationalStatus.Up);
    }

    private static bool SamePrefix(IPAddress left, IPAddress right, int prefixLength)
    {
        var a = left.GetAddressBytes();
        var b = right.GetAddressBytes();
        if (a.Length != b.Length || prefixLength <= 0 || prefixLength > a.Length * 8)
        {
            return false;
        }
        var fullBytes = prefixLength / 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (a[index] != b[index])
            {
                return false;
            }
        }
        var rest = prefixLength % 8;
        if (rest == 0)
        {
            return true;
        }
        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}
