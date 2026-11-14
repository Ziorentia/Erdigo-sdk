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
        var found = new DiscoveredServices(limit);
        await QueryAsync(new[] { new DnsQuestion(DnsWire.ServiceName, DnsType.Ptr) }, window, found, cancellationToken).ConfigureAwait(false);
        // Answers to the questions below only fill in what this first one found.
        found.CloseInstances();
        if (found.Instances.Count == 0)
        {
            return Array.Empty<ReceiverEndpoint>();
        }

        // A responder normally sends the service and its addresses along with the answer. Ask for whatever it did not.
        var missingServices = found.InstancesWithoutService();
        if (missingServices.Count > 0)
        {
            await QueryAsync(missingServices.Select(instance => new DnsQuestion(instance, DnsType.Srv)).ToList(), TimeSpan.FromSeconds(1), found, cancellationToken).ConfigureAwait(false);
        }
        var missingAddresses = found.HostsWithoutAddress();
        if (missingAddresses.Count > 0)
        {
            var questions = missingAddresses.SelectMany(target => new[] { new DnsQuestion(target, DnsType.A), new DnsQuestion(target, DnsType.Aaaa) }).ToList();
            await QueryAsync(questions, TimeSpan.FromSeconds(1), found, cancellationToken).ConfigureAwait(false);
        }
        return found.Endpoints();
    }

    /// <summary>
    /// Sends one query on every interface that can carry it, and hands <paramref name="found"/> the records in the
    /// answers that arrive within <paramref name="window"/>. Answers from beyond the local network are ignored.
    /// </summary>
    private static async Task QueryAsync(IReadOnlyList<DnsQuestion> questions, TimeSpan window, DiscoveredServices found, CancellationToken cancellationToken)
    {
        // Checked first as well, so that a browse that was cancelled stops even where no interface can send a query.
        cancellationToken.ThrowIfCancellationRequested();
        var id = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
        var query = DnsWire.Encode(new DnsMessage(id, false, false, questions, Array.Empty<DnsRecord>(), Array.Empty<DnsRecord>()));
        var sockets = OpenSockets(query);
        if (sockets.Count == 0)
        {
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(window);
        try
        {
            var listeners = sockets.Select(socket => ListenAsync(socket.Socket, id, found, stop.Token)).ToArray();
            // Asked again a little later, like a browser that keeps watching, so that a receiver that has only just
            // started advertising is found too.
            foreach (var delay in new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750) })
            {
                try
                {
                    await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                foreach (var (socket, destination) in sockets)
                {
                    try
                    {
                        socket.SendTo(query, destination);
                    }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException)
                    {
                    }
                }
            }
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (socket, _) in sockets)
            {
                socket.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ListenAsync(Socket socket, ushort id, DiscoveredServices found, CancellationToken stop)
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
            found.Add(message.AllRecords, from.Address);
        }
    }

    /// <summary>Opens one socket per interface and address family, sends the query on each, and returns them with where they sent it.</summary>
    private static List<(Socket Socket, IPEndPoint Destination)> OpenSockets(byte[] query)
    {
        var sockets = new List<(Socket, IPEndPoint)>();
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
                    var destination = new IPEndPoint(DnsWire.MulticastV4, DnsWire.MdnsPort);
                    socket.SendTo(query, destination);
                    sockets.Add((socket, destination));
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
                    var destination = new IPEndPoint(new IPAddress(DnsWire.MulticastV6.GetAddressBytes(), index), DnsWire.MdnsPort);
                    socket.SendTo(query, destination);
                    sockets.Add((socket, destination));
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

/// <summary>
/// What the answers to one browse have said, kept to what the browse can use. Each answer is read in order of
/// dependence: service instances, then their services, then the addresses of those services' hosts. So a later answer
/// can complete an earlier one, and a record nothing has asked about is not kept. However many answers arrive, and
/// however often they repeat themselves, this holds at most <c>limit</c> instances, one service for each, and a few
/// addresses for each of their hosts.
/// </summary>
/// <remarks>
/// A service or address that arrives before the record that leads to it is not kept. The follow-up questions in
/// <see cref="Discovery.BrowseAsync"/> ask for whatever is still missing by name.
/// </remarks>
internal sealed class DiscoveredServices
{
    /// <summary>Addresses kept for each host and address family. A receiver has one or two of each.</summary>
    internal const int AddressesPerFamily = 4;

    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private readonly object sync = new();
    private readonly int limit;
    private readonly List<string> instances = new();
    private readonly Dictionary<string, (string Target, ushort Port)> services = new(Names);
    private readonly Dictionary<string, List<IPAddress>> addresses = new(Names);
    private bool instancesClosed;

    public DiscoveredServices(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        this.limit = limit;
    }

    /// <summary>The service instances found, in the order they were first seen.</summary>
    public IReadOnlyList<string> Instances
    {
        get
        {
            lock (sync)
            {
                return instances.ToArray();
            }
        }
    }

    /// <summary>How many records are kept, for checking that this stays bounded.</summary>
    internal int KeptRecords
    {
        get
        {
            lock (sync)
            {
                return instances.Count + services.Count + addresses.Values.Sum(list => list.Count);
            }
        }
    }

    private static string Key(string name) => name.TrimEnd('.');

    /// <summary>Takes the records of one answer, which came from <paramref name="sender"/>.</summary>
    public void Add(IEnumerable<DnsRecord> records, IPAddress sender)
    {
        // An IPv6 link-local address works only on the interface it was learned on, which is known only when the
        // answer itself came over IPv6.
        var usable = new List<DnsRecord>();
        foreach (var record in records)
        {
            if (record.Address is not { IsIPv6LinkLocal: true } address)
            {
                usable.Add(record);
            }
            else if (sender.AddressFamily == AddressFamily.InterNetworkV6)
            {
                usable.Add(record with { Address = new IPAddress(address.GetAddressBytes(), sender.ScopeId) });
            }
        }

        lock (sync)
        {
            if (!instancesClosed)
            {
                foreach (var record in usable)
                {
                    if (instances.Count < limit && record.Type == DnsType.Ptr && DnsWire.NamesEqual(record.Name, DnsWire.ServiceName)
                        && record.Target is { } instance && record.Ttl > 0 && !instances.Contains(Key(instance), Names))
                    {
                        instances.Add(Key(instance));
                    }
                }
            }
            foreach (var record in usable)
            {
                // The first usable service for an instance stands, as a resolver would keep the first it heard.
                if (record.Type == DnsType.Srv && record.Target is { } target && record.Port != 0
                    && instances.Contains(Key(record.Name), Names) && !services.ContainsKey(Key(record.Name)))
                {
                    services[Key(record.Name)] = (Key(target), record.Port);
                }
            }
            var hosts = new HashSet<string>(services.Values.Select(service => service.Target), Names);
            foreach (var record in usable)
            {
                if (record.Address is not { } address || !hosts.Contains(Key(record.Name)))
                {
                    continue;
                }
                if (!addresses.TryGetValue(Key(record.Name), out var known))
                {
                    known = new List<IPAddress>();
                    addresses[Key(record.Name)] = known;
                }
                // The same address with another interface is another way to reach the host, so it is kept as well.
                if (!known.Contains(address) && known.Count(each => each.AddressFamily == address.AddressFamily) < AddressesPerFamily)
                {
                    known.Add(address);
                }
            }
        }
    }

    /// <summary>From now on no new instances are taken, only what completes the ones already found.</summary>
    public void CloseInstances()
    {
        lock (sync)
        {
            instancesClosed = true;
        }
    }

    public IReadOnlyList<string> InstancesWithoutService()
    {
        lock (sync)
        {
            return instances.Where(instance => !services.ContainsKey(instance)).ToList();
        }
    }

    public IReadOnlyList<string> HostsWithoutAddress()
    {
        lock (sync)
        {
            return services.Values.Select(service => service.Target).Distinct(Names).Where(host => !addresses.ContainsKey(host)).ToList();
        }
    }

    /// <summary>One address for each instance that has a service and an address, in the order the instances were found.</summary>
    public IReadOnlyList<ReceiverEndpoint> Endpoints()
    {
        lock (sync)
        {
            var endpoints = new List<ReceiverEndpoint>();
            foreach (var instance in instances)
            {
                if (!services.TryGetValue(instance, out var service) || !addresses.TryGetValue(service.Target, out var known))
                {
                    continue;
                }
                // IPv4 first, because an IPv6 link-local address needs the right interface to work.
                var address = known.OrderBy(candidate => candidate.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
                endpoints.Add(new ReceiverEndpoint(address.ToString(), service.Port));
            }
            return endpoints;
        }
    }
}
