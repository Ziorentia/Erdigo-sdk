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

namespace Erdigo.Sdk;

/// <summary>Where to look for a receiver.</summary>
public abstract class ReceiverTarget : IEquatable<ReceiverTarget>
{
    private ReceiverTarget()
    {
    }

    /// <summary>
    /// Try the remembered address, then up to eight receivers advertised on the local network. A receiver this device
    /// is not paired with stays silent. An advertisement proves nothing about who is behind it, so use
    /// <see cref="Address"/> where that matters. See the threat model.
    /// </summary>
    public static ReceiverTarget Discover { get; } = new DiscoverTarget();

    /// <summary>Try this address only.</summary>
    public static ReceiverTarget Address(string host, ushort port) => new AddressTarget(host, port);

    public sealed class AddressTarget : ReceiverTarget
    {
        internal AddressTarget(string host, ushort port)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Port = port;
        }

        public string Host { get; }

        public ushort Port { get; }
    }

    public sealed class DiscoverTarget : ReceiverTarget
    {
        internal DiscoverTarget()
        {
        }
    }

    public bool Equals(ReceiverTarget? other) => (this, other) switch
    {
        (AddressTarget mine, AddressTarget theirs) => mine.Host == theirs.Host && mine.Port == theirs.Port,
        (DiscoverTarget, DiscoverTarget) => true,
        _ => false,
    };

    public override bool Equals(object? obj) => Equals(obj as ReceiverTarget);

    public override int GetHashCode() => this is AddressTarget address ? HashCode.Combine(address.Host, address.Port) : 0;
}

/// <summary>An address to try.</summary>
public sealed record ReceiverEndpoint(string Host, ushort Port);

/// <summary>
/// Finds receivers that advertise <c>_erdigo-int._tcp</c>, with one-shot multicast DNS queries (RFC 6762 section
/// 5.1) sent from an ordinary port. Receivers answer such a query directly, so nothing here needs port 5353, and
/// nothing here is used when an address is given.
/// </summary>
internal static class Discovery
{
    /// <summary>Listens for receivers for a short time, and returns up to <paramref name="limit"/> of them.</summary>
    public static async Task<IReadOnlyList<ReceiverEndpoint>> BrowseAsync(TimeSpan? duration = null, int limit = 8, CancellationToken cancellationToken = default)
    {
        var window = duration ?? TimeSpan.FromSeconds(2);
        var records = await QueryAsync(new[] { new DnsQuestion(DnsWire.ServiceName, DnsType.Ptr) }, window, cancellationToken).ConfigureAwait(false);

        var instances = records
            .Where(record => record.Type == DnsType.Ptr && DnsWire.NamesEqual(record.Name, DnsWire.ServiceName) && record.Target is not null && record.Ttl > 0)
            .Select(record => record.Target!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        if (instances.Count == 0)
        {
            return Array.Empty<ReceiverEndpoint>();
        }

        // A responder normally sends the service and its addresses along with the answer. Ask for whatever it did not.
        var missingServices = instances.Where(instance => !records.Any(record => record.Type == DnsType.Srv && DnsWire.NamesEqual(record.Name, instance))).ToList();
        if (missingServices.Count > 0)
        {
            records.AddRange(await QueryAsync(missingServices.Select(instance => new DnsQuestion(instance, DnsType.Srv)).ToList(), TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false));
        }
        var targets = instances
            .Select(instance => records.FirstOrDefault(record => record.Type == DnsType.Srv && DnsWire.NamesEqual(record.Name, instance)))
            .Where(record => record?.Target is not null)
            .Select(record => record!.Target!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingAddresses = targets.Where(target => !records.Any(record => record.Address is not null && DnsWire.NamesEqual(record.Name, target))).ToList();
        if (missingAddresses.Count > 0)
        {
            var questions = missingAddresses.SelectMany(target => new[] { new DnsQuestion(target, DnsType.A), new DnsQuestion(target, DnsType.Aaaa) }).ToList();
            records.AddRange(await QueryAsync(questions, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false));
        }

        var endpoints = new List<ReceiverEndpoint>();
        foreach (var instance in instances)
        {
            var service = records.FirstOrDefault(record => record.Type == DnsType.Srv && DnsWire.NamesEqual(record.Name, instance));
            if (service?.Target is null || service.Port == 0)
            {
                continue;
            }
            // IPv4 first, because an IPv6 link-local address needs the right interface to work.
            var address = records
                .Where(record => record.Address is not null && DnsWire.NamesEqual(record.Name, service.Target))
                .Select(record => record.Address!)
                .OrderBy(candidate => candidate.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault();
            if (address is not null)
            {
                endpoints.Add(new ReceiverEndpoint(address.ToString(), service.Port));
            }
        }
        return endpoints;
    }

    /// <summary>
    /// Sends one query on every interface that can carry it, and collects every record in the answers that arrive
    /// within <paramref name="window"/>. Answers from beyond the local network are ignored.
    /// </summary>
    private static async Task<List<DnsRecord>> QueryAsync(IReadOnlyList<DnsQuestion> questions, TimeSpan window, CancellationToken cancellationToken)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
        var query = DnsWire.Encode(new DnsMessage(id, false, false, questions, Array.Empty<DnsRecord>(), Array.Empty<DnsRecord>()));
        var sockets = OpenSockets(query);
        var records = new List<DnsRecord>();
        if (sockets.Count == 0)
        {
            return records;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(window);
        try
        {
            var listeners = sockets.Select(socket => ListenAsync(socket, id, records, stop.Token)).ToArray();
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (records)
        {
            return records.ToList();
        }
    }

    private static async Task ListenAsync(Socket socket, ushort id, List<DnsRecord> records, CancellationToken stop)
    {
        var buffer = new byte[9000];
        var any = socket.AddressFamily == AddressFamily.InterNetwork ? (EndPoint)new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stop).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            if (result.RemoteEndPoint is not IPEndPoint from || from.Port != DnsWire.MdnsPort || !LocalNetwork.IsLocal(from.Address))
            {
                continue;
            }
            var message = DnsWire.TryDecode(buffer.AsSpan(0, result.ReceivedBytes));
            if (message is null || !message.IsResponse || message.Id != id)
            {
                continue;
            }
            var found = message.AllRecords.Select(record => record.Address is { IsIPv6LinkLocal: true } address
                ? record with { Address = new IPAddress(address.GetAddressBytes(), from.Address.ScopeId) }
                : record);
            lock (records)
            {
                records.AddRange(found);
            }
        }
    }

    private static List<Socket> OpenSockets(byte[] query)
    {
        var sockets = new List<Socket>();
        foreach (var networkInterface in LocalNetwork.UpInterfaces())
        {
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback || !networkInterface.SupportsMulticast)
            {
                continue;
            }
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                Socket? socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(unicast.Address, 0));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, unicast.Address.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    socket.SendTo(query, new IPEndPoint(DnsWire.MulticastV4, DnsWire.MdnsPort));
                    sockets.Add(socket);
                    socket = null;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    socket?.Dispose();
                }
            }
            if (Socket.OSSupportsIPv6 && properties.UnicastAddresses.Any(unicast => unicast.Address.IsIPv6LinkLocal))
            {
                Socket? socket = null;
                try
                {
                    var index = properties.GetIPv6Properties().Index;
                    socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                    socket.SendTo(query, new IPEndPoint(new IPAddress(DnsWire.MulticastV6.GetAddressBytes(), index), DnsWire.MdnsPort));
                    sockets.Add(socket);
                    socket = null;
                }
                catch (Exception error) when (error is SocketException or NetworkInformationException or PlatformNotSupportedException)
                {
                }
                finally
                {
                    socket?.Dispose();
                }
            }
        }
        return sockets;
    }

    /// <summary>The address given or remembered, which can be tried without looking around the network.</summary>
    public static IReadOnlyList<ReceiverEndpoint> KnownEndpoints(ReceiverTarget target, ReceiverPairing? pairing)
    {
        switch (target)
        {
            case ReceiverTarget.AddressTarget address:
                return new[] { Endpoint(address.Host, address.Port) };
            default:
                if (pairing?.LastHost is { } host && pairing.LastPort is { } port)
                {
                    return new[] { Endpoint(host, port) };
                }
                return Array.Empty<ReceiverEndpoint>();
        }
    }

    /// <summary>Every endpoint to try when pairing, where no address is remembered.</summary>
    public static async Task<IReadOnlyList<ReceiverEndpoint>> CandidatesAsync(ReceiverTarget target, CancellationToken cancellationToken)
    {
        var known = KnownEndpoints(target, null);
        return target is ReceiverTarget.DiscoverTarget ? await BrowseAsync(cancellationToken: cancellationToken).ConfigureAwait(false) : known;
    }

    public static ReceiverEndpoint Endpoint(string host, ushort port)
    {
        if (port == 0)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "the port must be between 1 and 65535");
        }
        return new ReceiverEndpoint(host, port);
    }
}
