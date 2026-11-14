using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;

namespace Erdigo.MockReceiver;

/// <summary>
/// Advertises the mock receiver as <c>_erdigo-int._tcp</c> with TXT <c>v=1</c> over multicast DNS (RFC 6762 and
/// RFC 6763), on IPv4. The instance name is random and changes every hour, as spec section 2.1 asks. Unlike the
/// system advertiser the Swift mock uses, it also gives the computer a random host name of its own, so the computer's
/// network name is not announced with it.
/// </summary>
/// <remarks>
/// It is a test tool: it answers queries and announces itself, but does not probe for name conflicts.
/// </remarks>
internal sealed class MdnsAdvertiser : IDisposable
{
    private const uint RecordTtl = 120;
    private const uint LegacyTtl = 10;

    private readonly ushort port;
    private readonly object sync = new();
    private readonly CancellationTokenSource stop = new();
    private Socket? socket;
    private string instance = string.Empty;
    private string host = string.Empty;
    private bool answering;

    public MdnsAdvertiser(ushort port)
    {
        this.port = port;
        NewNames();
    }

    public string InstanceName
    {
        get
        {
            lock (sync)
            {
                return instance;
            }
        }
    }

    private static string RandomHex(int bytes) => Hex.Encode(RandomNumberGenerator.GetBytes(bytes));

    private void NewNames()
    {
        instance = $"{RandomHex(8)}.{DnsWire.ServiceName}";
        host = $"erdigo-{RandomHex(6)}.local";
    }

    public void Start()
    {
        var created = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            SocketReuse.AllowSharing(created);
            created.Bind(new IPEndPoint(IPAddress.Any, DnsWire.MdnsPort));
            created.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            created.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            foreach (var address in InterfaceAddresses().SelectMany(entry => entry.Addresses).Distinct())
            {
                try
                {
                    created.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(DnsWire.MulticastV4, address));
                }
                catch (SocketException)
                {
                    // Already a member through another address of the same interface.
                }
            }
        }
        catch
        {
            created.Dispose();
            throw;
        }
        lock (sync)
        {
            socket = created;
            answering = true;
        }
        _ = ReceiveLoopAsync(created, stop.Token);
        Announce(RecordTtl);
    }

    /// <summary>Takes a new random name, and tells the network the old one is gone.</summary>
    public void Rotate()
    {
        Announce(0);
        lock (sync)
        {
            NewNames();
        }
        Announce(RecordTtl);
    }

    public void Pause()
    {
        Announce(0);
        lock (sync)
        {
            answering = false;
        }
    }

    public void Resume()
    {
        lock (sync)
        {
            NewNames();
            answering = true;
        }
        Announce(RecordTtl);
    }

    public void Dispose()
    {
        bool wasAnswering;
        lock (sync)
        {
            wasAnswering = answering;
        }
        if (wasAnswering)
        {
            Announce(0);
        }
        stop.Cancel();
        lock (sync)
        {
            answering = false;
            socket?.Dispose();
            socket = null;
        }
    }

    /// <summary>The IPv4 addresses of every interface that is up and can carry multicast, by interface index.</summary>
    private static List<(int Index, IReadOnlyList<IPAddress> Addresses)> InterfaceAddresses()
    {
        var result = new List<(int, IReadOnlyList<IPAddress>)>();
        foreach (var networkInterface in LocalNetwork.UpInterfaces())
        {
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback || !networkInterface.SupportsMulticast)
            {
                continue;
            }
            try
            {
                var properties = networkInterface.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(unicast => unicast.Address)
                    .ToList();
                if (addresses.Count > 0)
                {
                    result.Add((properties.GetIPv4Properties().Index, addresses));
                }
            }
            catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException)
            {
            }
        }
        return result;
    }

    private (List<DnsRecord> Answers, List<DnsRecord> Additionals) Records(DnsQuestion question, IReadOnlyList<IPAddress> addresses, uint ttl)
    {
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        var ptr = new DnsRecord(DnsWire.ServiceName, DnsType.Ptr, ttl, false) { Target = instance };
        var srv = new DnsRecord(instance, DnsType.Srv, ttl, true) { Target = host, Port = port };
        var txt = new DnsRecord(instance, DnsType.Txt, ttl, true) { Text = new[] { "v=1" } };
        var a = addresses.Select(address => new DnsRecord(host, DnsType.A, ttl, true) { Address = address }).ToList();
        var any = question.Type == DnsType.Any;
        if ((question.Type == DnsType.Ptr || any) && DnsWire.NamesEqual(question.Name, DnsWire.ServiceName))
        {
            answers.Add(ptr);
            additionals.Add(srv);
            additionals.Add(txt);
            additionals.AddRange(a);
        }
        if ((question.Type == DnsType.Srv || any) && DnsWire.NamesEqual(question.Name, instance))
        {
            answers.Add(srv);
            additionals.AddRange(a);
        }
        if ((question.Type == DnsType.Txt || any) && DnsWire.NamesEqual(question.Name, instance))
        {
            answers.Add(txt);
        }
        if ((question.Type == DnsType.A || any) && DnsWire.NamesEqual(question.Name, host))
        {
            answers.AddRange(a);
        }
        return (answers, additionals);
    }

    private async Task ReceiveLoopAsync(Socket receiving, CancellationToken cancellationToken)
    {
        var buffer = new byte[9000];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await receiving.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested || error is ObjectDisposedException)
                {
                    return;
                }
                continue;
            }
            if (result.RemoteEndPoint is not IPEndPoint from || !LocalNetwork.IsLocal(from.Address))
            {
                continue;
            }
            var query = DnsWire.TryDecode(buffer.AsSpan(0, result.ReceivedBytes));
            if (query is null || query.IsResponse || query.Questions.Count == 0)
            {
                continue;
            }
            try
            {
                Answer(receiving, query, from, result.PacketInformation.Interface);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or ArgumentException)
            {
            }
        }
    }

    private void Answer(Socket receiving, DnsMessage query, IPEndPoint from, int interfaceIndex)
    {
        var interfaces = InterfaceAddresses();
        var addresses = interfaces.FirstOrDefault(entry => entry.Index == interfaceIndex).Addresses
            ?? interfaces.SelectMany(entry => entry.Addresses).ToList();
        // A one-shot query comes from another port. It is answered directly, with its question repeated and short
        // lifetimes (RFC 6762 section 6.7).
        var legacy = from.Port != DnsWire.MdnsPort;
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        lock (sync)
        {
            if (!answering)
            {
                return;
            }
            foreach (var question in query.Questions)
            {
                var (found, extra) = Records(question, addresses, legacy ? LegacyTtl : RecordTtl);
                answers.AddRange(found);
                additionals.AddRange(extra);
            }
        }
        if (answers.Count == 0)
        {
            return;
        }
        additionals = additionals.Where(extra => !answers.Contains(extra)).Distinct().ToList();
        if (legacy)
        {
            static DnsRecord Plain(DnsRecord record) => record with { CacheFlush = false };
            var reply = new DnsMessage(query.Id, true, true, query.Questions, answers.Select(Plain).ToList(), additionals.Select(Plain).ToList());
            receiving.SendTo(DnsWire.Encode(reply), from);
            return;
        }
        var response = DnsWire.Encode(new DnsMessage(0, true, true, Array.Empty<DnsQuestion>(), answers, additionals));
        if (query.Questions.Any(question => question.UnicastResponse))
        {
            receiving.SendTo(response, from);
        }
        else
        {
            receiving.SendTo(response, new IPEndPoint(DnsWire.MulticastV4, DnsWire.MdnsPort));
        }
    }

    /// <summary>Tells every interface about this service, or with a lifetime of 0 that it is gone.</summary>
    private void Announce(uint ttl)
    {
        Socket? sending;
        lock (sync)
        {
            sending = socket;
        }
        if (sending is null)
        {
            return;
        }
        foreach (var (_, addresses) in InterfaceAddresses())
        {
            DnsMessage message;
            lock (sync)
            {
                var (answers, additionals) = Records(new DnsQuestion(DnsWire.ServiceName, DnsType.Ptr), addresses, ttl);
                message = new DnsMessage(0, true, true, Array.Empty<DnsQuestion>(), answers.Concat(additionals).ToList(), Array.Empty<DnsRecord>());
            }
            try
            {
                sending.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addresses[0].GetAddressBytes());
                sending.SendTo(DnsWire.Encode(message), new IPEndPoint(DnsWire.MulticastV4, DnsWire.MdnsPort));
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
