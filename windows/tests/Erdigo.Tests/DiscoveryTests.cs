using System;
using System.Linq;
using System.Net;
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
