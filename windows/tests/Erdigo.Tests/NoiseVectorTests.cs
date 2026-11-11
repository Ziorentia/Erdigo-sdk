using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erdigo.Core;
using Xunit;
using static Erdigo.Tests.VectorFiles;

namespace Erdigo.Tests;

public sealed class NoiseVectorTests
{
    private static readonly Dictionary<string, NoisePattern> Patterns = new()
    {
        ["Noise_IK_25519_ChaChaPoly_SHA256"] = NoisePattern.IK,
        ["Noise_XXpsk3_25519_ChaChaPoly_SHA256"] = new NoisePattern(
            "XXpsk3",
            responderStaticIsPreMessage: false,
            new[]
            {
                new[] { NoisePattern.Token.E },
                new[] { NoisePattern.Token.E, NoisePattern.Token.Ee, NoisePattern.Token.S, NoisePattern.Token.Es },
                new[] { NoisePattern.Token.S, NoisePattern.Token.Se, NoisePattern.Token.Psk },
            }),
        ["Noise_NNpsk0_25519_ChaChaPoly_SHA256"] = new NoisePattern(
            "NNpsk0",
            responderStaticIsPreMessage: false,
            new[]
            {
                new[] { NoisePattern.Token.Psk, NoisePattern.Token.E },
                new[] { NoisePattern.Token.E, NoisePattern.Token.Ee },
            }),
    };

    private static IEnumerable<byte[]> Psks(JsonElement vector, string name) =>
        vector.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(item => Bytes(item.GetString())).ToList() : Enumerable.Empty<byte[]>();

    /// <summary>Runs one official vector through both roles and compares every byte.</summary>
    private static void Run(JsonElement vector)
    {
        var name = vector.GetProperty("protocol_name").GetString()!;
        var pattern = Patterns[name];
        var initiator = new NoiseHandshake(
            pattern,
            isInitiator: true,
            Bytes(vector, "init_prologue"),
            OptionalBytes(vector, "init_static"),
            OptionalBytes(vector, "init_remote_static"),
            Psks(vector, "init_psks"),
            OptionalBytes(vector, "init_ephemeral"));
        var responder = new NoiseHandshake(
            pattern,
            isInitiator: false,
            Bytes(vector, "resp_prologue"),
            OptionalBytes(vector, "resp_static"),
            OptionalBytes(vector, "resp_remote_static"),
            Psks(vector, "resp_psks"),
            OptionalBytes(vector, "resp_ephemeral"));

        var messages = vector.GetProperty("messages").EnumerateArray().ToList();
        var index = 0;
        while (!initiator.IsComplete)
        {
            var payload = Bytes(messages[index], "payload");
            var expected = Bytes(messages[index], "ciphertext");
            var (writer, reader) = index % 2 == 0 ? (initiator, responder) : (responder, initiator);
            Assert.Equal(Hex.Encode(expected), Hex.Encode(writer.WriteMessage(payload)));
            Assert.Equal(Hex.Encode(payload), Hex.Encode(reader.ReadMessage(expected)));
            index += 1;
        }
        Assert.True(responder.IsComplete);

        var initiatorTransport = initiator.Split();
        var responderTransport = responder.Split();
        if (vector.TryGetProperty("handshake_hash", out var hash))
        {
            Assert.Equal(hash.GetString(), Hex.Encode(initiatorTransport.HandshakeHash));
            Assert.Equal(hash.GetString(), Hex.Encode(responderTransport.HandshakeHash));
        }

        // Continue alternating, as the official vectors do.
        while (index < messages.Count)
        {
            var payload = Bytes(messages[index], "payload");
            var expected = Bytes(messages[index], "ciphertext");
            var (sender, receiver) = index % 2 == 0 ? (initiatorTransport, responderTransport) : (responderTransport, initiatorTransport);
            Assert.Equal(Hex.Encode(expected), Hex.Encode(sender.Seal(payload)));
            Assert.Equal(Hex.Encode(payload), Hex.Encode(receiver.Open(expected)));
            index += 1;
        }
    }

    [Fact]
    public void OfficialVectors()
    {
        var file = Load("spec/vectors/noise/cacophony-subset.json");
        var vectors = file.GetProperty("vectors").EnumerateArray().ToList();
        Assert.Equal(3, vectors.Count);
        foreach (var vector in vectors)
        {
            Run(vector);
        }
    }

    internal static (NoiseTransport Device, NoiseTransport Receiver) EstablishedPair()
    {
        var deviceKey = Noise.NewPrivateKey();
        var receiverKey = Noise.NewPrivateKey();
        var prologue = Encoding.ASCII.GetBytes("test");
        var device = new NoiseHandshake(NoisePattern.IK, isInitiator: true, prologue, deviceKey, Noise.PublicKey(receiverKey));
        var receiver = new NoiseHandshake(NoisePattern.IK, isInitiator: false, prologue, receiverKey);
        receiver.ReadMessage(device.WriteMessage());
        device.ReadMessage(receiver.WriteMessage());
        return (device.Split(), receiver.Split());
    }

    [Fact]
    public void TamperedTransportMessageIsRejected()
    {
        var (device, receiver) = EstablishedPair();
        var sealedMessage = device.Seal(Encoding.ASCII.GetBytes("hello"));
        sealedMessage[0] ^= 0x01;
        Assert.Equal(NoiseError.DecryptionFailed, Assert.Throws<NoiseException>(() => receiver.Open(sealedMessage)).Error);
    }

    [Fact]
    public void ReplayedTransportMessageIsRejected()
    {
        var (device, receiver) = EstablishedPair();
        var sealedMessage = device.Seal(Encoding.ASCII.GetBytes("hello"));
        Assert.Equal("hello", Encoding.ASCII.GetString(receiver.Open(sealedMessage)));
        Assert.Throws<NoiseException>(() => receiver.Open(sealedMessage));
    }

    [Fact]
    public void OutOfOrderUseIsRejected()
    {
        var handshake = new NoiseHandshake(NoisePattern.IK, isInitiator: false, Array.Empty<byte>(), Noise.NewPrivateKey());
        Assert.Equal(NoiseError.WrongHandshakeState, Assert.Throws<NoiseException>(() => handshake.WriteMessage()).Error);
        Assert.Equal(NoiseError.WrongHandshakeState, Assert.Throws<NoiseException>(() => handshake.Split()).Error);
    }

    [Fact]
    public void PrologueMismatchFailsTheHandshake()
    {
        var receiverKey = Noise.NewPrivateKey();
        var device = new NoiseHandshake(NoisePattern.IK, isInitiator: true, IntegrationV1.Preamble(IntegrationV1.Mode.Connect), Noise.NewPrivateKey(), Noise.PublicKey(receiverKey));
        var receiver = new NoiseHandshake(NoisePattern.IK, isInitiator: false, IntegrationV1.Preamble(IntegrationV1.Mode.Pair), receiverKey);
        Assert.Throws<NoiseException>(() => receiver.ReadMessage(device.WriteMessage()));
    }

    [Fact]
    public void PairingFirstMessageFailsWithoutTheCode()
    {
        var prologue = IntegrationV1.Preamble(IntegrationV1.Mode.Pair);
        var right = PairingCode.Generate();
        var wrong = PairingCode.Generate();
        var device = new NoiseHandshake(NoisePattern.XXpsk0, isInitiator: true, prologue, Noise.NewPrivateKey(), psk: wrong.PresharedKey());
        var receiver = new NoiseHandshake(NoisePattern.XXpsk0, isInitiator: false, prologue, Noise.NewPrivateKey(), psk: right.PresharedKey());
        Assert.Equal(NoiseError.DecryptionFailed, Assert.Throws<NoiseException>(() => receiver.ReadMessage(device.WriteMessage())).Error);
    }

    /// <summary>Public keys of small order produce an all-zero secret. Without this check an attacker could force a key that everyone knows.</summary>
    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0100000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800")]
    [InlineData("5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157")]
    [InlineData("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]
    [InlineData("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]
    [InlineData("eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]
    public void LowOrderPublicKeysAreRejected(string point)
    {
        var key = Noise.NewPrivateKey();
        Assert.Equal(NoiseError.WeakSharedSecret, Assert.Throws<NoiseException>(() => Noise.Dh(key, Bytes(point))).Error);
    }

    [Fact]
    public void CanonicalKeyRule()
    {
        var key = Noise.PublicKey(Noise.NewPrivateKey());
        Assert.True(Noise.IsCanonicalPublicKey(key));
        key[31] |= 0x80;
        Assert.False(Noise.IsCanonicalPublicKey(key));
        Assert.False(Noise.IsCanonicalPublicKey(Bytes("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")));
        Assert.True(Noise.IsCanonicalPublicKey(Bytes("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")));
        Assert.False(Noise.IsCanonicalPublicKey(new byte[31]));
    }
}

public sealed class IntegrationVectorTests
{
    private static void Replay(string name, NoisePattern pattern, bool psk)
    {
        var vector = Load($"spec/vectors/integration-v1/{name}");
        var prologue = Bytes(vector, "preamble_and_prologue");
        var psks = psk ? new[] { Bytes(vector, "psk") } : Array.Empty<byte[]>();

        if (psk)
        {
            var code = new PairingCode(vector.GetProperty("pairing_code_as_typed").GetString()!);
            Assert.Equal(vector.GetProperty("pairing_code_canonical").GetString(), code.Canonical);
            Assert.Equal(Hex.Encode(psks[0]), Hex.Encode(code.PresharedKey()));
            var ticket = PairingTicket.Parse(vector.GetProperty("qr_text").GetString()!);
            Assert.Equal(code, ticket.Code);
            Assert.Equal(IntegrationV1.Fingerprint(Bytes(vector, "receiver_static_public")), ticket.Fingerprint);
        }
        Assert.Equal(Hex.Encode(Bytes(vector, "device_static_public")), Hex.Encode(Noise.PublicKey(Bytes(vector, "device_static_private"))));
        Assert.Equal(Hex.Encode(Bytes(vector, "receiver_static_public")), Hex.Encode(Noise.PublicKey(Bytes(vector, "receiver_static_private"))));

        var device = new NoiseHandshake(
            pattern, isInitiator: true, prologue, Bytes(vector, "device_static_private"),
            psk ? null : Bytes(vector, "receiver_static_public"), psks, Bytes(vector, "device_ephemeral_private"));
        var receiver = new NoiseHandshake(
            pattern, isInitiator: false, prologue, Bytes(vector, "receiver_static_private"),
            null, psks, Bytes(vector, "receiver_ephemeral_private"));

        NoiseTransport? deviceTransport = null;
        NoiseTransport? receiverTransport = null;
        foreach (var frame in vector.GetProperty("frames").EnumerateArray())
        {
            var stage = frame.GetProperty("stage").GetString();
            var wire = Bytes(frame, "wire");
            if (stage == "preamble")
            {
                Assert.Equal(prologue, wire);
                continue;
            }
            var payload = Bytes(frame, "payload");
            var noise = Bytes(frame, "noise_message");
            Assert.Equal(wire, ByteStream.Frame(noise));
            var fromDevice = frame.GetProperty("direction").GetString() == "device_to_receiver";

            if (!device.IsComplete || !receiver.IsComplete)
            {
                var (writer, reader) = fromDevice ? (device, receiver) : (receiver, device);
                Assert.Equal(Hex.Encode(noise), Hex.Encode(writer.WriteMessage(payload)));
                Assert.Equal(Hex.Encode(payload), Hex.Encode(reader.ReadMessage(noise)));
                if (device.IsComplete && receiver.IsComplete)
                {
                    deviceTransport = device.Split();
                    receiverTransport = receiver.Split();
                    Assert.Equal(vector.GetProperty("handshake_hash").GetString(), Hex.Encode(deviceTransport.HandshakeHash));
                }
            }
            else if (fromDevice)
            {
                Assert.Equal(Hex.Encode(noise), Hex.Encode(deviceTransport!.Seal(payload)));
                Assert.Equal(Hex.Encode(payload), Hex.Encode(receiverTransport!.Open(noise)));
                if (stage == "hello")
                {
                    Wire.DecodeHello(payload);
                }
                else
                {
                    Wire.DecodeRequest(payload);
                }
            }
            else
            {
                Assert.Equal(Hex.Encode(noise), Hex.Encode(receiverTransport!.Seal(payload)));
                Assert.Equal(Hex.Encode(payload), Hex.Encode(deviceTransport!.Open(noise)));
                Wire.DecodeReceiverMessage(payload);
            }
        }
        Assert.True(device.IsComplete);
    }

    [Fact]
    public void ConnectVector() => Replay("handshake-connect.json", NoisePattern.IK, psk: false);

    [Fact]
    public void PairVector() => Replay("handshake-pair.json", NoisePattern.XXpsk0, psk: true);

    [Fact]
    public void VectorKeysAreDerivedFromPublicLabels()
    {
        var vector = Load("spec/vectors/integration-v1/handshake-connect.json");
        var expected = Hex.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("erdigo-integration-v1 test-only key: device static")));
        Assert.Equal(expected, vector.GetProperty("device_static_private").GetString());
        Assert.Equal(vector.GetProperty("device_fingerprint").GetString(), IntegrationV1.Fingerprint(Bytes(vector, "device_static_public")));
        Assert.Equal(vector.GetProperty("receiver_fingerprint").GetString(), IntegrationV1.Fingerprint(Bytes(vector, "receiver_static_public")));
    }

    [Fact]
    public void FirstConnectMessageCarriesTheCounterAndHasTheRequiredLength()
    {
        var vector = Load("spec/vectors/integration-v1/handshake-connect.json");
        var first = vector.GetProperty("frames")[1];
        Assert.Equal(IntegrationV1.ConnectFirstMessageLength, Bytes(first, "noise_message").Length);
        Assert.Equal(vector.GetProperty("connect_counter").GetUInt64(), System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(Bytes(first, "payload")));
        var pair = Load("spec/vectors/integration-v1/handshake-pair.json");
        Assert.Equal(IntegrationV1.PairFirstMessageLength, Bytes(pair.GetProperty("frames")[1], "noise_message").Length);
    }

    /// <summary>This SDK writes the vectors' messages byte for byte, as the Swift SDK does.</summary>
    [Fact]
    public void MessagesAreEncodedByteForByteLikeTheVectors()
    {
        string Payload(string file, string stage) =>
            Load($"spec/vectors/integration-v1/{file}").GetProperty("frames").EnumerateArray()
                .First(frame => frame.GetProperty("stage").GetString() == stage).GetProperty("payload_text").GetString()!;
        string Text(byte[] data) => Encoding.UTF8.GetString(data);

        Assert.Equal(Payload("handshake-connect.json", "hello"), Text(Wire.Encode(new Hello(new[] { 1 }, "erdigo-test/1.0"))));
        Assert.Equal(
            Payload("handshake-connect.json", "welcome"),
            Text(Wire.Encode(new ReceiverMessage.WelcomeMessage(new Welcome(1, 41, new[] { ItemKind.Pin, ItemKind.Track }, Limits.V1)))));
        Assert.Equal(
            Payload("handshake-connect.json", "request"),
            Text(Wire.Encode(new RequestEnvelope(42, new Request.PutPin(new ItemId("gate"), new Coordinate(37.5796, 126.977), "Palace gate")))));
        Assert.Equal(Payload("handshake-connect.json", "ack"), Text(Wire.Encode(new ReceiverMessage.Ack(42, false))));
        Assert.Equal(
            Payload("handshake-pair.json", "handshake_3"),
            Text(Wire.Encode(new PairRequest(new[] { 1 }, "erdigo-test/1.0", "Test device", new[] { ItemKind.Pin, ItemKind.Track }))));
        Assert.Equal(Payload("handshake-pair.json", "pair_status_pending"), Text(Wire.Encode(new ReceiverMessage.PairStatusMessage(PairStatus.Pending.Instance))));
        Assert.Equal(
            Payload("handshake-pair.json", "pair_status_approved"),
            Text(Wire.Encode(new ReceiverMessage.PairStatusMessage(new PairStatus.Approved(new[] { ItemKind.Pin }, 0)))));
    }

    [Fact]
    public void MessageVectors()
    {
        var file = Load("spec/vectors/integration-v1/messages.json");
        foreach (var entry in file.GetProperty("valid").EnumerateArray())
        {
            var json = Encoding.UTF8.GetBytes(entry.GetProperty("json").GetString()!);
            var envelope = Wire.DecodeRequest(json);
            Assert.True(envelope.Seq >= 1, entry.GetProperty("name").GetString());
        }
        var invalid = file.GetProperty("invalid").EnumerateArray().ToList();
        Assert.NotEmpty(invalid);
        foreach (var entry in invalid)
        {
            var name = entry.GetProperty("name").GetString();
            var error = Assert.Throws<WireException>(() => Wire.DecodeRequest(Encoding.UTF8.GetBytes(entry.GetProperty("json").GetString()!)));
            Assert.True(entry.GetProperty("code").GetString() == error.Code.ToWire(), $"{name}: expected {entry.GetProperty("code").GetString()}, got {error.Code.ToWire()}");
        }
        foreach (var entry in file.GetProperty("invalid_bytes").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            var error = Assert.Throws<WireException>(() => Wire.DecodeRequest(Bytes(entry, "hex")));
            Assert.True(entry.GetProperty("code").GetString() == error.Code.ToWire(), $"{name}: expected {entry.GetProperty("code").GetString()}, got {error.Code.ToWire()}");
        }
    }
}
