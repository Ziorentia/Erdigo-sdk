using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.MockReceiver;
using Erdigo.Sdk;
using Xunit;

namespace Erdigo.Tests;

public sealed class DiscoveryTests
{
    private const string Instance = "0123456789abcdef._erdigo-int._tcp.local";
    private const string Host = "erdigo-0123456789ab.local";

    private static byte[] Answer() => DnsWire.Encode(new DnsMessage(
        7,
        IsResponse: true,
        Authoritative: true,
        new[] { new DnsQuestion(DnsWire.ServiceName, DnsType.Ptr) },
        new[] { new DnsRecord(DnsWire.ServiceName, DnsType.Ptr, 10, false) { Target = Instance } },
        new[]
        {
            new DnsRecord(Instance, DnsType.Srv, 10, true) { Target = Host, Port = 4242 },
            new DnsRecord(Instance, DnsType.Txt, 10, true) { Text = new[] { "v=1" } },
            new DnsRecord(Host, DnsType.A, 10, true) { Address = IPAddress.Parse("192.0.2.10") },
        }));

    [Fact]
    public void MessagesRoundTrip()
    {
        var message = DnsWire.TryDecode(Answer());
        Assert.NotNull(message);
        Assert.True(message.IsResponse);
        Assert.Equal(7, message.Id);
        Assert.Equal(DnsWire.ServiceName, Assert.Single(message.Questions).Name);
        Assert.Equal(Instance, Assert.Single(message.Answers).Target);
        var service = message.Additionals.Single(record => record.Type == DnsType.Srv);
        Assert.Equal((Host, (ushort)4242), (service.Target, service.Port));
        Assert.Equal(new[] { "v=1" }, message.Additionals.Single(record => record.Type == DnsType.Txt).Text);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), message.Additionals.Single(record => record.Type == DnsType.A).Address);
    }

    [Fact]
    public void CompressedNamesAreRead()
    {
        // A PTR answer whose target points back at the question's name, as real responders write it.
        var bytes = new byte[]
        {
            0, 1, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            3, (byte)'a', (byte)'b', (byte)'c', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0, 0, 12, 0, 1,
            0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 10, 0, 6, 3, (byte)'x', (byte)'y', (byte)'z', 0xC0, 12,
        };
        var message = DnsWire.TryDecode(bytes);
        Assert.NotNull(message);
        Assert.Equal("abc.local", message.Answers[0].Name);
        Assert.Equal("xyz.abc.local", message.Answers[0].Target);
    }

    [Fact]
    public void HostileMessagesAreRefused()
    {
        var good = Answer();
        // Every truncation of a valid message is refused rather than read past its end.
        for (var length = 0; length < good.Length; length++)
        {
            Assert.Null(DnsWire.TryDecode(good.AsSpan(0, length)));
        }
        // A name pointer that points at itself, or forwards, would loop or read ahead.
        var loop = new byte[] { 0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1 };
        Assert.Null(DnsWire.TryDecode(loop));
        var forward = new byte[] { 0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 20, 0, 12, 0, 1, 0, 0 };
        Assert.Null(DnsWire.TryDecode(forward));
        // A label type this reader does not know.
        var label = new byte[] { 0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x40, 0, 0, 12, 0, 1 };
        Assert.Null(DnsWire.TryDecode(label));
        // A name longer than 255 bytes.
        var longName = new byte[] { 0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0 }
            .Concat(Enumerable.Repeat(new byte[] { 63 }.Concat(Enumerable.Repeat((byte)'a', 63)), 5).SelectMany(part => part))
            .Concat(new byte[] { 0, 0, 12, 0, 1 }).ToArray();
        Assert.Null(DnsWire.TryDecode(longName));
        // A record that claims more data than there is.
        var claim = good.ToArray();
        claim[^6] = 0x00;
        claim[^5] = 0xFF;
        Assert.Null(DnsWire.TryDecode(claim));
        // An error response is of no use.
        var failed = good.ToArray();
        failed[3] |= 0x03;
        Assert.Null(DnsWire.TryDecode(failed));
    }

    [Fact]
    public void RandomBytesNeverThrow()
    {
        var random = new Random(1);
        for (var round = 0; round < 20_000; round++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            if (bytes.Length > 4)
            {
                bytes[2] &= 0x87;
                bytes[3] &= 0xF0;
            }
            DnsWire.TryDecode(bytes);
        }
    }

    [Fact]
    public void TheLocalNetworkIsRecognised()
    {
        Assert.True(LocalNetwork.IsLocal(IPAddress.Loopback));
        Assert.True(LocalNetwork.IsLocal(IPAddress.Parse("169.254.10.20")));
        Assert.True(LocalNetwork.IsLocal(IPAddress.Parse("fe80::1")));
        Assert.True(LocalNetwork.IsLocal(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(LocalNetwork.IsLocal(IPAddress.Parse("8.8.8.8")));
        Assert.False(LocalNetwork.IsLocal(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public async Task AnAddressNeedsNoDiscovery()
    {
        var target = ReceiverTarget.Address("127.0.0.1", 4242);
        Assert.Equal(new[] { new ReceiverEndpoint("127.0.0.1", 4242) }, Discovery.KnownEndpoints(target, null));
        Assert.Equal(new[] { new ReceiverEndpoint("127.0.0.1", 4242) }, await Discovery.CandidatesAsync(target, default));
        Assert.Throws<ErdigoException>(() => Discovery.KnownEndpoints(ReceiverTarget.Address("127.0.0.1", 0), null));
        var remembered = new ReceiverPairing("x", new byte[32], new[] { ItemKind.Pin }, "192.0.2.1", 7);
        Assert.Equal(new[] { new ReceiverEndpoint("192.0.2.1", 7) }, Discovery.KnownEndpoints(ReceiverTarget.Discover, remembered));
    }

    // Collecting answers

    private static readonly IPAddress FromV4 = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress FromV6 = new(IPAddress.Parse("fe80::2").GetAddressBytes(), 5);

    private static DnsRecord Ptr(string instance, uint ttl = 10) => new(DnsWire.ServiceName, DnsType.Ptr, ttl, false) { Target = instance };

    private static DnsRecord Srv(string instance, string host, ushort port) => new(instance, DnsType.Srv, 10, true) { Target = host, Port = port };

    private static DnsRecord Address(string host, string address) =>
        new(host, IPAddress.Parse(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? DnsType.A : DnsType.Aaaa, 10, true) { Address = IPAddress.Parse(address) };

    private static string InstanceNamed(int number) => $"{number:x16}._erdigo-int._tcp.local";

    [Fact]
    public void ManyRepeatedAndUnaskedRecordsKeepABoundedAmount()
    {
        var found = new DiscoveredServices(limit: 8);
        var random = new Random(3);
        for (var round = 0; round < 5_000; round++)
        {
            var records = new List<DnsRecord>
            {
                Ptr(Instance),
                Srv(Instance, Host, 4242),
                new(Instance, DnsType.Txt, 10, true) { Text = new[] { "v=1" } },
                Address(Host, "192.0.2.10"),
                Address(Host, $"10.0.{random.Next(256)}.{random.Next(256)}"),
                Address(Host, $"2001:db8::{random.Next(65536):x}"),
                // Names nothing asked about.
                Srv($"other{round}._erdigo-int._tcp.local", $"other{round}.local", 1),
                Address($"other{round}.local", "192.0.2.99"),
            };
            if (round % 10 == 0)
            {
                // More instances than are wanted, each complete.
                var instance = InstanceNamed(round);
                var host = $"host{round}.local";
                records.AddRange(new[] { Ptr(instance), Srv(instance, host, 1000), Address(host, "192.0.2.20") });
            }
            found.Add(records, FromV4);
        }

        // At most eight instances, one service each, and four addresses of each family for each host.
        Assert.Equal(8, found.Instances.Count);
        Assert.InRange(found.KeptRecords, 8, 8 * (1 + 1 + 2 * DiscoveredServices.AddressesPerFamily));
        var endpoints = found.Endpoints();
        Assert.Equal(8, endpoints.Count);
        // The first address heard for the first receiver is still the one used.
        Assert.Equal(new ReceiverEndpoint("192.0.2.10", 4242), endpoints[0]);
    }

    [Fact]
    public void SeveralReceiversAreFoundInTheOrderTheyAnswered()
    {
        var found = new DiscoveredServices(limit: 8);
        found.Add(new[] { Ptr("b._erdigo-int._tcp.local"), Srv("b._erdigo-int._tcp.local", "b.local", 2000), Address("b.local", "192.0.2.2") }, FromV4);
        found.Add(new[] { Ptr("a._erdigo-int._tcp.local"), Srv("a._erdigo-int._tcp.local", "a.local", 1000), Address("a.local", "192.0.2.1") }, FromV4);
        // The same answer again changes nothing.
        found.Add(new[] { Ptr("b._erdigo-int._tcp.local"), Srv("b._erdigo-int._tcp.local", "b.local", 2000), Address("b.local", "192.0.2.2") }, FromV4);
        Assert.Equal(new[] { new ReceiverEndpoint("192.0.2.2", 2000), new ReceiverEndpoint("192.0.2.1", 1000) }, found.Endpoints());
        Assert.Equal(6, found.KeptRecords);
    }

    [Fact]
    public void LaterAnswersCompleteEarlierOnes()
    {
        var found = new DiscoveredServices(limit: 8);
        // Names differ in case and in a final dot, as they may between responders.
        found.Add(new[] { Ptr(Instance + ".") }, FromV4);
        Assert.Equal(new[] { Instance }, found.InstancesWithoutService());
        found.CloseInstances();

        // The answer to the question about the service, with an instance nobody asked about.
        found.Add(new[] { Srv(Instance.ToUpperInvariant(), Host, 4242), Ptr("late._erdigo-int._tcp.local") }, FromV4);
        Assert.Empty(found.InstancesWithoutService());
        Assert.Equal(new[] { Host }, found.HostsWithoutAddress());
        Assert.Empty(found.Endpoints());

        // The answer to the question about the host's addresses.
        found.Add(new[] { Address(Host.ToUpperInvariant() + ".", "192.0.2.10") }, FromV4);
        Assert.Empty(found.HostsWithoutAddress());
        Assert.Equal(new[] { new ReceiverEndpoint("192.0.2.10", 4242) }, found.Endpoints());
        Assert.Equal(new[] { Instance }, found.Instances);
    }

    [Fact]
    public void RecordsNothingLedToAreNotKeptAndAreAskedForLater()
    {
        var found = new DiscoveredServices(limit: 8);
        // A service and an address that arrive before the record pointing at them.
        found.Add(new[] { Srv(Instance, Host, 4242), Address(Host, "192.0.2.10") }, FromV4);
        Assert.Equal(0, found.KeptRecords);
        found.Add(new[] { Ptr(Instance) }, FromV4);
        // So the follow-up question asks for the service by name.
        Assert.Equal(new[] { Instance }, found.InstancesWithoutService());
    }

    [Fact]
    public void InstancesStopAtTheLimitAndGoodbyesAreIgnored()
    {
        var found = new DiscoveredServices(limit: 3);
        found.Add(new[] { Ptr(InstanceNamed(0), ttl: 0) }, FromV4);
        found.Add(Enumerable.Range(1, 20).Select(number => Ptr(InstanceNamed(number))), FromV4);
        Assert.Equal(new[] { InstanceNamed(1), InstanceNamed(2), InstanceNamed(3) }, found.Instances);
        Assert.Equal(3, found.KeptRecords);
    }

    [Fact]
    public void ServiceWithoutAPortDoesNotHideALaterOne()
    {
        var found = new DiscoveredServices(limit: 8);
        found.Add(new[] { Ptr(Instance), Srv(Instance, Host, 0) }, FromV4);
        found.Add(new[] { Srv(Instance, Host, 4242), Srv(Instance, "elsewhere.local", 1), Address(Host, "192.0.2.10") }, FromV4);
        Assert.Equal(new[] { new ReceiverEndpoint("192.0.2.10", 4242) }, found.Endpoints());
    }

    [Fact]
    public void IPv6LinkLocalAddressesKeepTheInterfaceTheyCameFrom()
    {
        var found = new DiscoveredServices(limit: 8);
        found.Add(new[] { Ptr(Instance), Srv(Instance, Host, 4242) }, FromV4);

        // Over IPv4 the interface is unknown, so a link-local address is of no use.
        found.Add(new[] { Address(Host, "fe80::10") }, FromV4);
        Assert.Equal(new[] { Host }, found.HostsWithoutAddress());

        // Over IPv6 it takes the interface the answer came in on, and the same address on two interfaces is two ways in.
        found.Add(new[] { Address(Host, "fe80::10"), Address(Host, "fe80::10") }, FromV6);
        found.Add(new[] { Address(Host, "fe80::10") }, new IPAddress(IPAddress.Parse("fe80::3").GetAddressBytes(), 7));
        Assert.Equal(new ReceiverEndpoint("fe80::10%5", 4242), Assert.Single(found.Endpoints()));
        Assert.Equal(1 + 1 + 2, found.KeptRecords);

        // An IPv4 address is still preferred when there is one, wherever it was heard.
        found.Add(new[] { Address(Host, "192.0.2.10") }, FromV6);
        Assert.Equal(new ReceiverEndpoint("192.0.2.10", 4242), Assert.Single(found.Endpoints()));
    }

    [Fact]
    public async Task ACancelledBrowseStops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discovery.BrowseAsync(cancellationToken: cancelled.Token));

        // Cancelling partway does not wait out the time given for answers. Without an interface that can send a
        // query, the browse ends at once with nothing found, which is fine here too.
        using var later = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();
        try
        {
            Assert.Empty(await Discovery.BrowseAsync(TimeSpan.FromSeconds(30), cancellationToken: later.Token));
        }
        catch (OperationCanceledException)
        {
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    /// <summary>
    /// Advertises with the mock's responder and finds it with a one-shot query, over real multicast. Machines without
    /// a multicast-capable interface, or whose network drops multicast, cannot run this, so it runs only when
    /// <c>ERDIGO_MULTICAST_TESTS=1</c> is set in the environment of the test run.
    /// </summary>
    [Fact]
    public async Task TheMockIsFoundByAOneShotQuery()
    {
        if (Environment.GetEnvironmentVariable("ERDIGO_MULTICAST_TESTS") != "1")
        {
            return;
        }
        using var advertiser = new MdnsAdvertiser(4242);
        advertiser.Start();
        var found = await Discovery.BrowseAsync();
        Assert.Contains(found, endpoint => endpoint.Port == 4242);
    }
}
