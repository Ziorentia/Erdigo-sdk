using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;
using Xunit;
using Receiver = Erdigo.MockReceiver.MockReceiver;

namespace Erdigo.Tests;

public sealed class EndToEndTests : EndToEndBase
{
    // Everything working

    [Fact]
    public async Task PairThenEveryOperation()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var fingerprint = device.Identity.Fingerprint;
        Assert.Equal(receiver.Fingerprint, device.Pairing.Fingerprint);

        await device.Client.PingAsync();
        await device.Client.PutPinAsync("pin-1", Palace, "Palace");
        await device.Client.PutMarkerAsync("marker-1", Palace, "Zürich €", MarkerColor.Blue);
        await device.Client.PutNoteAsync("note-1", Palace, "First line\nhttps://example.com/ stays plain text");
        await device.Client.MoveAsync("pin-1", CityHall);
        var points = Enumerable.Range(0, 250).Select(index => new TrackPoint(Palace, index)).ToArray();
        await device.Client.AppendTrackAsync("walk", points, "Walk");
        await device.Client.FinishTrackAsync("walk");
        await device.Client.PutPhotoAsync("photo-1", Palace, TestImages.Jpeg, "View");

        var items = receiver.Items(fingerprint);
        Assert.Equal(new[] { "marker-1", "note-1", "photo-1", "pin-1", "walk" }, items.Select(item => item.Id));
        var pin = items.Single(item => item.Id == "pin-1");
        Assert.Equal(CityHall.Latitude, pin.Latitude);
        Assert.Equal("Palace", pin.Title);
        var walk = items.Single(item => item.Id == "walk");
        Assert.Equal(250, walk.Points.Count);
        Assert.True(walk.IsFinished);
        Assert.Equal("blue", items.Single(item => item.Id == "marker-1").Color);

        await device.Client.DeleteAsync("pin-1");
        await device.Client.DeleteAsync("pin-1");
        Assert.Equal(4, receiver.Items(fingerprint).Count);
        await device.Client.DeleteAllAsync();
        Assert.Empty(receiver.Items(fingerprint));
        device.Client.Close();
    }

    [Fact]
    public async Task PersonMayAllowFewerKindsAndRename()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.SetApprovalHandler((request, _) =>
        {
            Assert.Equal("Test device", request.DeviceName);
            return Task.FromResult(Receiver.ApprovalDecision.Allow(new[] { ItemKind.Pin, ItemKind.Photo }, "Renamed"));
        });
        var device = await PairedDeviceAsync(receiver, port, new[] { ItemKind.Pin, ItemKind.Marker });
        Assert.Equal(new[] { ItemKind.Pin }, device.Pairing.Kinds);
        Assert.Equal("Renamed", receiver.PairedDevices().First().Name);

        await device.Client.PutPinAsync("a", Palace);
        var error = await Assert.ThrowsAsync<ErdigoException>(() => device.Client.PutMarkerAsync("b", Palace));
        Assert.Equal(ErdigoErrorKind.NotPermitted, error.Kind);
        Assert.Equal(ItemKind.Marker, error.ItemKind);
    }

    [Fact]
    public async Task ReceiverRefusesKindsTheDeviceWasNotAllowed()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port, new[] { ItemKind.Pin });
        var raw = await AuthenticatedAsync(device, receiver, port);
        var reply = await raw.SendAsync("{\"t\":\"put\",\"seq\":1,\"kind\":\"marker\",\"id\":\"m\",\"lat\":1,\"lon\":1}");
        Assert.Equal(ErrorCode.NotPermitted.ToWire(), reply.Code);
        Assert.Empty(receiver.Items(device.Identity.Fingerprint));
        raw.Stream.Cancel();
    }

    [Fact]
    public async Task DeviceNameIsTreatedAsUntrusted()
    {
        var (receiver, port) = await StartReceiverAsync();
        var identity = DeviceIdentity.Generate();
        var ticket = receiver.OpenPairingWindow();

        // Skips the SDK, which would have cleaned the name itself.
        var stream = await RawConnectAsync(port);
        var prologue = IntegrationV1.Preamble(IntegrationV1.Mode.Pair);
        var handshake = new NoiseHandshake(NoisePattern.XXpsk0, isInitiator: true, prologue, identity.PrivateKey, psk: ticket.Code.PresharedKey());
        await stream.SendAsync(Pairing.Concat(prologue, ByteStream.Frame(handshake.WriteMessage())));
        handshake.ReadMessage(await stream.ReadFrameAsync());
        var hostile = "Bank‮cod.exe\u0007\n" + new string('x', 60);
        var request = new PairRequest(new[] { 1 }, "test/1", hostile, new[] { ItemKind.Pin });
        await stream.SendFrameAsync(handshake.WriteMessage(Wire.Encode(request)));
        await BytesUntilCloseAsync(stream);

        var name = receiver.PairedDevices().First().Name;
        Assert.Equal(40, name.EnumerateRunes().Count());
        Assert.DoesNotContain(name.EnumerateRunes(), scalar => scalar.Value is 0x202E or 0x07 or 0x0A);
    }

    // Silence towards strangers

    [Fact]
    public async Task UnknownDeviceReceivesNothing()
    {
        var (receiver, port) = await StartReceiverAsync();
        var stream = await RawConnectAsync(port);
        await stream.SendAsync(ConnectMessage(Noise.NewPrivateKey(), receiver.PublicKey, 1).Bytes);
        Assert.Equal(0, await BytesUntilCloseAsync(stream));
    }

    [Fact]
    public async Task DeviceThatDoesNotKnowTheReceiverKeyReceivesNothing()
    {
        var (_, port) = await StartReceiverAsync();
        var stream = await RawConnectAsync(port);
        await stream.SendAsync(ConnectMessage(Noise.NewPrivateKey(), Noise.PublicKey(Noise.NewPrivateKey()), 1).Bytes);
        Assert.Equal(0, await BytesUntilCloseAsync(stream));
    }

    [Fact]
    public async Task GarbageReceivesNothing()
    {
        var (_, port) = await StartReceiverAsync();
        var connect = IntegrationV1.Preamble(IntegrationV1.Mode.Connect);
        var pair = IntegrationV1.Preamble(IntegrationV1.Mode.Pair);
        foreach (var garbage in new[]
        {
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"),
            new byte[5],
            connect.Concat(new byte[] { 0xFF, 0xFF }).Concat(Enumerable.Repeat((byte)0xAA, 200)).ToArray(),
            Pairing.Concat(connect, ByteStream.Frame(new byte[104])),
            Pairing.Concat(pair, ByteStream.Frame(new byte[48])),
        })
        {
            var stream = await RawConnectAsync(port);
            await stream.SendAsync(garbage);
            Assert.Equal(0, await BytesUntilCloseAsync(stream));
        }
    }

    [Fact]
    public async Task LowOrderEphemeralKeyReceivesNothing()
    {
        var (_, port) = await StartReceiverAsync();
        var stream = await RawConnectAsync(port);
        var frame = ByteStream.Frame(new byte[32].Concat(Enumerable.Repeat((byte)0x11, 72)).ToArray());
        await stream.SendAsync(Pairing.Concat(IntegrationV1.Preamble(IntegrationV1.Mode.Connect), frame));
        Assert.Equal(0, await BytesUntilCloseAsync(stream));
    }

    [Fact]
    public async Task ReplayedHandshakeReceivesNothing()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var (_, captured) = ConnectMessage(device.Identity.PrivateKey, receiver.PublicKey, 5000);

        var first = await RawConnectAsync(port);
        await first.SendAsync(captured);
        Assert.NotEmpty(await first.ReadFrameAsync());
        first.Cancel();

        var replay = await RawConnectAsync(port);
        await replay.SendAsync(captured);
        // A replayed message must not reveal that this receiver knows the device.
        Assert.Equal(0, await BytesUntilCloseAsync(replay));
    }

    [Fact]
    public async Task ReplayCannotDisplaceALiveConnection()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.PingAsync();

        var farFuture = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000_000;
        var (_, message) = ConnectMessage(device.Identity.PrivateKey, receiver.PublicKey, farFuture);
        var attacker = await RawConnectAsync(port);
        await attacker.SendAsync(message);
        await attacker.ReadFrameAsync();
        // The attacker cannot produce a valid hello, so the old connection must survive.
        await attacker.SendFrameAsync(Enumerable.Repeat((byte)0x41, 64).ToArray());
        await BytesUntilCloseAsync(attacker);

        await device.Client.PutPinAsync("still-here", Palace);
    }

    [Fact]
    public async Task RetryPolicyNoneReportsNoAnswer()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.Stop();
        var store = new InMemoryDeviceStore();
        var pairing = new ReceiverPairing("x", receiver.PublicKey, new[] { ItemKind.Pin });
        var client = new ErdigoClient(store.LoadOrCreateIdentity(), pairing, store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(0.5));
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => client.PingAsync());
    }

    // Pairing rules

    [Fact]
    public async Task WrongCodeLeavesTheRightCodeWorking()
    {
        var (receiver, port) = await StartReceiverAsync();
        var right = receiver.OpenPairingWindow();

        var stranger = await RawConnectAsync(port);
        await stranger.SendAsync(PairingFirstMessage(PairingCode.Generate(), out _));
        // Someone without the code must not learn the receiver key.
        Assert.Equal(0, await BytesUntilCloseAsync(stranger));

        var pairing = await PairAsync(port, right, name: "Real device");
        Assert.Equal(new[] { ItemKind.Pin }, pairing.Kinds);
    }

    [Fact]
    public async Task ClosedWindowIsSilentAndAsksNobody()
    {
        var (receiver, port) = await StartReceiverAsync();
        var asked = false;
        receiver.SetApprovalHandler((_, _) =>
        {
            asked = true;
            return Task.FromResult(Receiver.ApprovalDecision.Deny);
        });
        var ticket = receiver.OpenPairingWindow();
        receiver.ClosePairingWindow();
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => PairAsync(port, ticket, name: "Late", handshakeTimeout: TimeSpan.FromSeconds(0.5)));
        Assert.False(asked);
    }

    [Fact]
    public async Task ExpiredWindowIsSilent()
    {
        var options = FastOptions();
        options.PairingWindow = TimeSpan.FromMilliseconds(50);
        var (receiver, port) = await StartReceiverAsync(options);
        var ticket = receiver.OpenPairingWindow();
        await Task.Delay(100);
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => PairAsync(port, ticket, name: "Late", handshakeTimeout: TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public async Task DeniedRequestStoresNothing()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.SetApprovalHandler((_, _) => Task.FromResult(Receiver.ApprovalDecision.Deny));
        var ticket = receiver.OpenPairingWindow();
        await ExpectAsync(ErdigoErrorKind.PairingDenied, () => PairAsync(port, ticket, name: "Unwanted"));
        Assert.Empty(receiver.PairedDevices());
    }

    [Fact]
    public async Task NobodyAnsweringDenies()
    {
        var options = FastOptions();
        options.ApprovalTimeout = TimeSpan.FromMilliseconds(100);
        var (receiver, port) = await StartReceiverAsync(options);
        receiver.SetApprovalHandler(async (_, _) =>
        {
            await Task.Delay(5000);
            return Receiver.ApprovalDecision.Allow(new[] { ItemKind.Pin });
        });
        var ticket = receiver.OpenPairingWindow();
        await ExpectAsync(ErdigoErrorKind.PairingDenied, () => PairAsync(port, ticket, name: "Nobody home"));
    }

    [Fact]
    public async Task CodeUsedTwiceRaisesAlarmAndRemovesThePairing()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var thief = DeviceIdentity.Generate();
        await PairAsync(port, ticket, thief, "Thief");
        Assert.Single(receiver.PairedDevices());

        await ExpectAsync(ErdigoErrorKind.CodeReused, () => PairAsync(port, ticket, name: "Owner"));
        // The pairing made with the stolen code must be removed.
        Assert.Empty(receiver.PairedDevices());
        Assert.Contains(new Receiver.Event.CodeReusedAlarm(thief.Fingerprint), receiver.Events);
    }

    /// <summary>Someone holding the code must not be able to switch off the alarm by trying many times.</summary>
    [Fact]
    public async Task ManyAttemptsWithTheCodeRaiseTheAlarmRatherThanHideIt()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var thief = DeviceIdentity.Generate();
        await PairAsync(port, ticket, thief, "Thief");

        for (var attempt = 0; attempt < 70; attempt++)
        {
            var stream = await RawConnectAsync(port);
            await stream.SendAsync(PairingFirstMessage(ticket.Code, out _));
            try
            {
                await stream.ReadFrameAsync();
            }
            catch (IOException)
            {
            }
            stream.Cancel();
        }

        await ExpectAsync(ErdigoErrorKind.CodeReused, () => PairAsync(port, ticket, name: "Owner"));
        Assert.Contains(new Receiver.Event.CodeReusedAlarm(thief.Fingerprint), receiver.Events);
        Assert.Empty(receiver.PairedDevices());
    }

    /// <summary>Without the code, recordings must not be able to raise the alarm or remove a real pairing.</summary>
    [Fact]
    public async Task ReplayingManyRecordingsCannotRemoveAPairing()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var recordings = Enumerable.Range(0, 70).Select(index => PairingFirstMessage(ticket.Code, out _)).ToList();
        var identity = DeviceIdentity.Generate();
        await PairAsync(port, ticket, identity, "Real");
        foreach (var recording in recordings)
        {
            var stream = await RawConnectAsync(port);
            await stream.SendAsync(recording);
            try
            {
                await stream.ReadFrameAsync();
            }
            catch (IOException)
            {
            }
            stream.Cancel();
        }
        Assert.Equal(new[] { identity.Fingerprint }, receiver.PairedDevices().Select(device => device.Fingerprint));
        Assert.DoesNotContain(receiver.Events, happened => happened is Receiver.Event.CodeReusedAlarm);
    }

    [Fact]
    public async Task SameDeviceRetryingPairingIsNotAnAlarm()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var identity = DeviceIdentity.Generate();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pairing = await PairAsync(port, ticket, identity, "Retry");
            Assert.Equal(new[] { ItemKind.Pin }, pairing.Kinds);
        }
        Assert.DoesNotContain(receiver.Events, happened => happened is Receiver.Event.CodeReusedAlarm);
    }

    [Fact]
    public async Task AStuckRetryLoopOnOneDeviceIsNotAnAlarm()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var identity = DeviceIdentity.Generate();
        for (var attempt = 0; attempt < 70; attempt++)
        {
            await PairAsync(port, ticket, identity, "Looping");
        }
        Assert.Single(receiver.PairedDevices());
        Assert.DoesNotContain(receiver.Events, happened => happened is Receiver.Event.CodeReusedAlarm);
    }

    [Fact]
    public async Task StallingAfterTheHandshakeReplyIsPenalised()
    {
        var options = FastOptions();
        options.AddressPenaltyBase = TimeSpan.FromSeconds(30);
        options.HandshakeTimeout = TimeSpan.FromSeconds(0.3);
        options.PenalisedHandshakeTimeout = TimeSpan.FromSeconds(0.05);
        var (receiver, port) = await StartReceiverAsync(options);
        var ticket = receiver.OpenPairingWindow();
        var stream = await RawConnectAsync(port);
        await stream.SendAsync(PairingFirstMessage(ticket.Code, out _));
        await stream.ReadFrameAsync();
        await BytesUntilCloseAsync(stream);
        // Now under a penalty: the next quiet connection gets the short deadline.
        Assert.True(await SecondsUntilCloseAsync(port, IntegrationV1.Preamble(IntegrationV1.Mode.Connect)) < 0.25);
    }

    [Fact]
    public async Task TwoDevicesRacingWithOneCodeRaiseAlarm()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.SetApprovalHandler(async (request, _) =>
        {
            await Task.Delay(200);
            return Receiver.ApprovalDecision.Allow(request.RequestedKinds);
        });
        var ticket = receiver.OpenPairingWindow();
        await Task.WhenAll(TryPairAsync(port, ticket), TryPairAsync(port, ticket));
        // Neither device may stay paired after the code was used twice.
        Assert.Empty(receiver.PairedDevices());
        Assert.Contains(receiver.Events, happened => happened is Receiver.Event.CodeReusedAlarm);
    }

    [Fact]
    public async Task FingerprintMismatchStopsBeforeTheDeviceIdentifiesItself()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var wrong = new PairingTicket(ticket.Code, new string('0', 64));
        await ExpectAsync(ErdigoErrorKind.FingerprintMismatch, () => PairAsync(port, wrong, name: "Careful"));
        await AssertHeardNoRequestAsync(receiver);
    }

    [Fact]
    public async Task ATypedCodeAsksBeforeTheReceiverHearsAnything()
    {
        var (receiver, port) = await StartReceiverAsync();
        var code = receiver.OpenPairingWindow().Code;
        var shortKey = IntegrationV1.ShortFingerprint(receiver.Fingerprint);

        // Nothing has reached the person at the receiver while the question is on the screen.
        var yes = new KeyQuestion(true, async () =>
        {
            await Task.Delay(500);
            Assert.DoesNotContain(receiver.Events, happened => happened is Receiver.Event.PairingRequested);
            Assert.Empty(receiver.PairedDevices());
        });
        var identity = DeviceIdentity.Generate();
        var pairing = await PairAsync(port, new PairingTicket(code), identity, "Careful", confirmKey: yes.AskAsync);

        Assert.Equal(new[] { shortKey }, yes.Shown);
        Assert.Equal(receiver.Fingerprint, pairing.Fingerprint);
        // Only the key that really pairs is known to the receiver.
        Assert.Equal(new[] { identity.Fingerprint }, receiver.PairedDevices().Select(device => device.Fingerprint));
    }

    [Fact]
    public async Task SayingNoLeavesNothingBehindAndTheCodeStillWorks()
    {
        var (receiver, port) = await StartReceiverAsync();
        var code = receiver.OpenPairingWindow().Code;
        var shortKey = IntegrationV1.ShortFingerprint(receiver.Fingerprint);

        var no = new KeyQuestion(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await ExpectAsync(ErdigoErrorKind.KeyNotConfirmed, () => PairAsync(port, new PairingTicket(code), name: "Careful", confirmKey: no.AskAsync));
        }
        Assert.Equal(new[] { shortKey, shortKey }, no.Shown);
        await AssertHeardNoRequestAsync(receiver);

        // The person looks again and says yes, and the same code works.
        var yes = new KeyQuestion(true);
        var pairing = await PairAsync(port, new PairingTicket(code), name: "Careful", confirmKey: yes.AskAsync);
        Assert.Equal(shortKey, IntegrationV1.ShortFingerprint(pairing.Fingerprint));
    }

    [Fact]
    public async Task SayingNoWhenPairingAgainLeavesTheReceiversRecordAlone()
    {
        var (receiver, port) = await StartReceiverAsync();
        var identity = DeviceIdentity.Generate();
        var first = receiver.OpenPairingWindow();
        await PairAsync(port, first, identity, "Careful", new[] { ItemKind.Pin, ItemKind.Marker });

        var second = receiver.OpenPairingWindow().Code;
        var no = new KeyQuestion(false);
        await ExpectAsync(ErdigoErrorKind.KeyNotConfirmed, () => PairAsync(port, new PairingTicket(second), identity, "Careful", confirmKey: no.AskAsync));
        await Task.Delay(500);
        Assert.Equal(new[] { ItemKind.Pin, ItemKind.Marker }, receiver.PairedDevices().Single().Kinds);
    }

    [Fact]
    public async Task ATypedCodeCannotPairWithoutTheQuestion()
    {
        var (receiver, port) = await StartReceiverAsync();
        var code = receiver.OpenPairingWindow().Code;
        await ExpectAsync(ErdigoErrorKind.InvalidArgument, () => PairAsync(port, new PairingTicket(code), name: "Careless"));
        await AssertHeardNoRequestAsync(receiver);
    }

    [Fact]
    public async Task ATicketWithAFingerprintAsksNothing()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var question = new KeyQuestion(false);
        var pairing = await PairAsync(port, ticket, name: "Scanner", confirmKey: question.AskAsync);
        Assert.Empty(question.Shown);
        Assert.Equal(ticket.Fingerprint, pairing.Fingerprint);
    }

    /// <summary>Someone else who knows the code takes the receiver's place between the question and the pairing.</summary>
    [Fact]
    public async Task AKeyOtherThanTheOneConfirmedDoesNotPair()
    {
        var code = new PairingCode("TEST0-0NLYC-0DE00-00000");
        var (real, port) = await StartReceiverAsync();
        real.OpenPairingWindow(code);
        var impostorOptions = FastOptions();
        impostorOptions.Port = port;
        var impostor = Track(new Receiver(impostorOptions));
        impostor.OpenPairingWindow(code);

        var swap = new KeyQuestion(true, async () =>
        {
            real.Stop();
            // The port takes a moment to come free.
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    await impostor.StartAsync();
                    return;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    await Task.Delay(50);
                }
            }
            Assert.Fail("the second receiver could not take over the port");
        });
        await ExpectAsync(ErdigoErrorKind.FingerprintMismatch, () => PairAsync(port, new PairingTicket(code), name: "Careful", confirmKey: swap.AskAsync));
        Assert.Single(swap.Shown);
        await AssertHeardNoRequestAsync(impostor);
    }

    [Fact]
    public async Task NobodyAnswersForTheWrongCodeOrWithNoWindowAndNothingIsAsked()
    {
        var (receiver, port) = await StartReceiverAsync();
        var wrong = new PairingCode("TEST0-0NLYC-0DE00-00000");
        var question = new KeyQuestion(true);
        // No window is open at first, and then one with another code.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await ExpectAsync(ErdigoErrorKind.NoAnswer, () => PairAsync(port, new PairingTicket(wrong), name: "Careful", confirmKey: question.AskAsync));
            receiver.OpenPairingWindow();
        }
        Assert.Empty(question.Shown);
        await AssertHeardNoRequestAsync(receiver);
    }

    [Fact]
    public async Task QrTextFromTheReceiverPairsAndAChangedOneIsRefused()
    {
        var (receiver, port) = await StartReceiverAsync();
        var first = receiver.OpenPairingWindow();
        var scanned = PairingTicket.Parse(first.Uri!);
        Assert.Equal(first, scanned);
        var pairing = await PairAsync(port, scanned, name: "Scanner");
        Assert.Equal(receiver.Fingerprint, pairing.Fingerprint);

        var second = receiver.OpenPairingWindow();
        var changed = PairingTicket.Parse($"erdigo-pair:1?code={second.Code.Display}&fp={new string('0', 64)}");
        await ExpectAsync(ErdigoErrorKind.FingerprintMismatch, () => PairAsync(port, changed, name: "Scanner"));
        await Task.Delay(500);
        // Only the correct QR text may lead to a pairing.
        Assert.Single(receiver.PairedDevices());
    }

    // Removing a device

    [Fact]
    public async Task RemovedDeviceIsCutOffWithinOneSecondAndStaysOut()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);

        var started = DateTime.UtcNow;
        receiver.Revoke(device.Identity.Fingerprint);
        Assert.Equal(0, await BytesUntilCloseAsync(raw.Stream, 3));
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 1);

        // A removed device must not get back in.
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => device.Client.PingAsync());
    }

    [Fact]
    public async Task RemovedDeviceCanReturnOnlyWithANewCodeAndConfirmation()
    {
        var (receiver, port) = await StartReceiverAsync();
        var store = new InMemoryDeviceStore();
        var identity = store.LoadOrCreateIdentity();
        var pairing = await PairAsync(port, receiver.OpenPairingWindow(), identity, "D");
        store.Save(pairing);

        var client = new ErdigoClient(identity, pairing, store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(1));
        await client.PutPinAsync("kept", Palace);
        client.Close();
        receiver.Revoke(identity.Fingerprint);
        // Items stay unless the person asked to delete them.
        Assert.Equal(new[] { "kept" }, receiver.Items(identity.Fingerprint).Select(item => item.Id));

        var sawRemoved = false;
        receiver.SetApprovalHandler((request, _) =>
        {
            sawRemoved |= request.WasRemovedBefore;
            return Task.FromResult(Receiver.ApprovalDecision.Allow(request.RequestedKinds));
        });
        await PairAsync(port, receiver.OpenPairingWindow(), identity, "D");
        Assert.True(sawRemoved);

        var again = new ErdigoClient(identity, pairing, store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(1));
        await again.PingAsync();
        Assert.Equal(new[] { "kept" }, receiver.Items(identity.Fingerprint).Select(item => item.Id));
    }

    [Fact]
    public async Task PauseClosesEverythingAndResumeWorks()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.PingAsync();
        receiver.SetPaused(true);
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => device.Client.PingAsync());
        receiver.SetPaused(false);
        await device.Client.PingAsync();
    }

    // Isolation between devices

    [Fact]
    public async Task DeviceCannotTouchAnotherDevicesItems()
    {
        var (receiver, port) = await StartReceiverAsync();
        var alice = await PairedDeviceAsync(receiver, port);
        var bob = await PairedDeviceAsync(receiver, port);

        await alice.Client.PutPinAsync("shared-name", Palace, "Alice");
        await bob.Client.PutPinAsync("shared-name", CityHall, "Bob");
        await ExpectRejectedAsync(ErrorCode.NotFound, () => bob.Client.MoveAsync("only-alice", CityHall));

        await bob.Client.DeleteAsync("shared-name");
        await bob.Client.DeleteAllAsync();

        var aliceItems = receiver.Items(alice.Identity.Fingerprint);
        Assert.Equal("Alice", aliceItems.First().Title);
        Assert.Equal(Palace.Latitude, aliceItems.First().Latitude);
        Assert.Empty(receiver.Items(bob.Identity.Fingerprint));
    }

    // Sequence numbers

    [Fact]
    public async Task LostAcknowledgementIsSafeToResend()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        const string request = "{\"t\":\"track_append\",\"seq\":1,\"id\":\"walk\",\"points\":[{\"lat\":1,\"lon\":1}]}";

        var first = await raw.SendAsync(request);
        Assert.Equal(1UL, first.Ack);
        Assert.False(first.Duplicate);

        var second = await raw.SendAsync(request);
        Assert.Equal(1UL, second.Ack);
        Assert.True(second.Duplicate);

        // A resend must not apply twice.
        Assert.Single(receiver.Items(device.Identity.Fingerprint).First().Points);
    }

    [Fact]
    public async Task SameNumberWithDifferentContentIsRejected()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        await raw.SendAsync("{\"t\":\"put\",\"seq\":1,\"kind\":\"pin\",\"id\":\"a\",\"lat\":1,\"lon\":1}");
        var reply = await raw.SendAsync("{\"t\":\"put\",\"seq\":1,\"kind\":\"pin\",\"id\":\"a\",\"lat\":2,\"lon\":2}");
        Assert.Equal(ErrorCode.SeqConflict.ToWire(), reply.Code);
        Assert.Equal(1.0, receiver.Items(device.Identity.Fingerprint).First().Latitude);
    }

    [Fact]
    public async Task OldNumberIsRejectedAndReportsTheLatest()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        await raw.SendAsync("{\"t\":\"ping\",\"seq\":10}");
        var reply = await raw.SendAsync("{\"t\":\"ping\",\"seq\":9}");
        Assert.Equal(ErrorCode.StaleSeq.ToWire(), reply.Code);
        Assert.Equal(10UL, reply.LastSeq);
    }

    [Fact]
    public async Task RejectedRequestKeepsItsAnswerOnResend()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        const string request = "{\"t\":\"move\",\"seq\":1,\"id\":\"missing\",\"lat\":1,\"lon\":1}";
        Assert.Equal(ErrorCode.NotFound.ToWire(), (await raw.SendAsync(request)).Code);
        Assert.Equal(ErrorCode.NotFound.ToWire(), (await raw.SendAsync(request)).Code);
    }

    [Fact]
    public async Task NewClientWithNoMemoryContinuesNumbering()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.PingAsync();
        await device.Client.PingAsync();
        device.Client.Close();

        var forgetful = new ErdigoClient(device.Identity, device.Pairing, device.Store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(1));
        await forgetful.PingAsync();
        Assert.Equal(3UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task ClientRecoversWhenTheConnectionDropsMidway()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.UpTo(TimeSpan.FromSeconds(10)));
        await device.Client.PingAsync();

        receiver.SetPaused(true);
        var resume = Task.Run(async () =>
        {
            await Task.Delay(300);
            receiver.SetPaused(false);
        });
        await device.Client.PutPinAsync("after-sleep", Palace);
        await resume;
        Assert.Equal(new[] { "after-sleep" }, receiver.Items(device.Identity.Fingerprint).Select(item => item.Id));
    }

    [Fact]
    public async Task PhotoRestartsAfterTheConnectionDrops()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.UpTo(TimeSpan.FromSeconds(10)));
        var raw = await AuthenticatedAsync(device, receiver, port);
        var photo = TestImages.Jpeg;
        var digest = Hex.Encode(System.Security.Cryptography.SHA256.HashData(photo));
        await raw.SendAsync($"{{\"t\":\"photo_begin\",\"seq\":1,\"id\":\"p\",\"lat\":1,\"lon\":1,\"type\":\"image/jpeg\",\"size\":{photo.Length},\"sha256\":\"{digest}\",\"chunks\":1}}");
        raw.Stream.Cancel();
        await Task.Delay(50);

        await device.Client.PutPhotoAsync("p", Palace, photo);
        Assert.Equal(ItemKind.Photo, receiver.Items(device.Identity.Fingerprint).First().Kind);
    }

    // Connection rules

    [Fact]
    public async Task NewConnectionReplacesTheOldOne()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var old = await AuthenticatedAsync(device, receiver, port);
        await device.Client.PingAsync();

        var frame = await old.Stream.ReadFrameAsync();
        var message = Assert.IsType<ReceiverMessage.Error>(Wire.DecodeReceiverMessage(old.Transport.Open(frame)));
        Assert.Equal(ErrorCode.Replaced.ToWire(), message.Value.Code);
        Assert.True(message.Value.Fatal);
    }

    [Fact]
    public async Task UnsupportedVersionIsReported()
    {
        var options = FastOptions();
        options.AcceptedVersions = new[] { 1, 2 };
        var (flexible, port) = await StartReceiverAsync(options);
        var identity = DeviceIdentity.Generate();
        var pairing = await PairAsync(port, flexible.OpenPairingWindow(), identity, "Old SDK");

        // An SDK that only speaks a version the receiver has dropped gets a clear answer.
        var raw = await AuthenticatedAsync(identity, pairing.ReceiverKey, port, new[] { 99 }, expectWelcome: false);
        var frame = await raw.Stream.ReadFrameAsync();
        var error = Assert.IsType<ReceiverMessage.Error>(Wire.DecodeReceiverMessage(raw.Transport.Open(frame)));
        Assert.Equal(ErrorCode.VersionUnsupported.ToWire(), error.Value.Code);
        Assert.Equal(new[] { 1, 2 }, error.Value.Supported);
    }

    [Fact]
    public async Task LargestMessageWorksOverTcpAndALargerOneCannotBeSent()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);

        // White space after the object is valid JSON, so this is a ping of exactly the largest size.
        const string ping = "{\"t\":\"ping\",\"seq\":1}";
        var largest = ping + new string(' ', IntegrationV1.MaxPlaintextLength - ping.Length);
        Assert.Equal(1UL, (await raw.SendAsync(largest)).Ack);

        // One byte more does not fit in a frame.
        Assert.Equal(NoiseError.MessageTooLarge, Assert.Throws<NoiseException>(() => raw.Transport.Seal(new byte[IntegrationV1.MaxPlaintextLength + 1])).Error);
    }

    [Fact]
    public async Task PeerThatStopsReadingIsDisconnected()
    {
        var options = FastOptions();
        options.SendTimeout = TimeSpan.FromSeconds(0.5);
        // Only the peer's silence may end this connection.
        options.Limits = options.Limits with { MessagesPerSecond = 10_000_000, MessageBurst = 10_000_000 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var fingerprint = device.Identity.Fingerprint;

        var (handshake, first) = ConnectMessage(device.Identity.PrivateKey, receiver.PublicKey, NextRawCounter());
        using var peer = new PlainSocketPeer(port);
        peer.WriteAll(first);
        handshake.ReadMessage(peer.ReadFrame());
        var transport = handshake.Split();
        peer.WriteAll(ByteStream.Frame(transport.Seal(Wire.Encode(new Hello(new[] { 1 }, "test/1")))));
        peer.ReadFrame();

        // Requests from here on and no more reading, so the answers soon have nowhere to go.
        peer.StopWaitingOnWrites();
        var unsent = new List<byte>();
        var seq = 1UL;
        var wasDisconnected = false;
        var giveUp = DateTime.UtcNow.AddSeconds(20);
        while (!wasDisconnected && DateTime.UtcNow < giveUp)
        {
            while (unsent.Count < 16_384)
            {
                unsent.AddRange(ByteStream.Frame(transport.Seal(Encoding.ASCII.GetBytes($"{{\"t\":\"ping\",\"seq\":{seq}}}"))));
                seq += 1;
            }
            if (peer.WriteSome(unsent.ToArray()) is { } taken && taken > 0)
            {
                unsent.RemoveRange(0, taken);
                continue;
            }
            await Task.Delay(20);
            wasDisconnected = receiver.Events.Contains(new Receiver.Event.Disconnected(fingerprint));
        }
        // The receiver must not wait for ever on a peer that has stopped reading.
        Assert.True(wasDisconnected);
    }

    [Fact]
    public async Task PreAuthLimitPerAddress()
    {
        var options = FastOptions();
        options.MaxPreAuthConnections = 8;
        options.MaxPreAuthConnectionsPerAddress = 2;
        options.HandshakeTimeout = TimeSpan.FromSeconds(3);
        var (_, port) = await StartReceiverAsync(options);

        var held = new List<ByteStream> { await RawConnectAsync(port), await RawConnectAsync(port) };
        await Task.Delay(100);
        // The first two are being waited for. The third is closed at once.
        Assert.True(await SecondsUntilCloseAsync(port, Array.Empty<byte>()) < 0.5);
        held.ForEach(stream => stream.Cancel());
    }

    [Fact]
    public async Task PreAuthLimitInTotal()
    {
        var options = FastOptions();
        options.MaxPreAuthConnections = 1;
        options.MaxPreAuthConnectionsPerAddress = 8;
        options.HandshakeTimeout = TimeSpan.FromSeconds(3);
        var (_, port) = await StartReceiverAsync(options);

        var held = await RawConnectAsync(port);
        await Task.Delay(100);
        Assert.True(await SecondsUntilCloseAsync(port, Array.Empty<byte>()) < 0.5);
        held.Cancel();
    }

    [Fact]
    public async Task PendingApprovalDoesNotHoldASlot()
    {
        var options = FastOptions();
        options.MaxPreAuthConnections = 1;
        options.MaxPreAuthConnectionsPerAddress = 1;
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);

        var release = new TaskCompletionSource();
        receiver.SetApprovalHandler(async (_, _) =>
        {
            await release.Task;
            return Receiver.ApprovalDecision.Deny;
        });
        var ticket = receiver.OpenPairingWindow();
        var waiting = TryPairAsync(port, ticket);
        await Task.Delay(200);

        // Someone waiting for a person must not keep a paired device out.
        await device.Client.PingAsync();
        release.SetResult();
        await waiting;
    }

    /// <summary>The receiver's state must not show in how long a connection stays open.</summary>
    [Fact]
    public async Task CloseTimingDoesNotRevealTheReceiversState()
    {
        var options = FastOptions();
        options.HandshakeTimeout = TimeSpan.FromSeconds(0.4);
        var (receiver, port) = await StartReceiverAsync(options);
        var pair = IntegrationV1.Preamble(IntegrationV1.Mode.Pair);
        var connect = IntegrationV1.Preamble(IntegrationV1.Mode.Connect);
        var oversized = new byte[] { 0xFF, 0xFF };
        Task<double> Measure(byte[] bytes) => SecondsUntilCloseAsync(port, bytes);

        // A preamble and then nothing: the deadline in every state.
        var quiet = new List<double> { await Measure(pair) };
        receiver.OpenPairingWindow();
        quiet.Add(await Measure(pair));
        receiver.ClosePairingWindow();
        quiet.Add(await Measure(connect));
        receiver.SetPaused(true);
        quiet.Add(await Measure(connect));
        receiver.SetPaused(false);
        Assert.All(quiet, seconds => Assert.InRange(seconds, 0.15, 0.65));

        // A frame too large to read: closed quickly in every state.
        var large = new List<double> { await Measure(pair.Concat(oversized).ToArray()) };
        receiver.OpenPairingWindow();
        large.Add(await Measure(pair.Concat(oversized).ToArray()));
        receiver.ClosePairingWindow();
        large.Add(await Measure(connect.Concat(oversized).ToArray()));
        receiver.SetPaused(true);
        large.Add(await Measure(connect.Concat(oversized).ToArray()));
        receiver.SetPaused(false);
        Assert.All(large, seconds => Assert.True(seconds < 0.2, $"{seconds}"));
    }

    [Fact]
    public async Task EveryFailedAttemptIsPenalised()
    {
        var options = FastOptions();
        options.AddressPenaltyBase = TimeSpan.FromSeconds(30);
        options.HandshakeTimeout = TimeSpan.FromSeconds(2);
        options.PenalisedHandshakeTimeout = TimeSpan.FromSeconds(0.2);
        foreach (var bytes in new[]
        {
            Encoding.ASCII.GetBytes("garbage"),
            IntegrationV1.Preamble(IntegrationV1.Mode.Connect).Concat(new byte[] { 0xFF, 0xFF }).ToArray(),
            Pairing.Concat(IntegrationV1.Preamble(IntegrationV1.Mode.Pair), ByteStream.Frame(Enumerable.Repeat((byte)1, 48).ToArray())),
        })
        {
            var (receiver, port) = await StartReceiverAsync(options);
            var first = await RawConnectAsync(port);
            await first.SendAsync(bytes);
            await BytesUntilCloseAsync(first);
            Assert.Contains(receiver.Events, happened => happened is Receiver.Event.SilentlyDropped);
            // The next attempt from this address gets much less time.
            Assert.True(await SecondsUntilCloseAsync(port, Array.Empty<byte>()) < 1);
        }
    }

    [Fact]
    public async Task TimingOutIsPenalised()
    {
        var options = FastOptions();
        options.AddressPenaltyBase = TimeSpan.FromSeconds(30);
        options.HandshakeTimeout = TimeSpan.FromSeconds(0.3);
        options.PenalisedHandshakeTimeout = TimeSpan.FromSeconds(0.05);
        var (_, port) = await StartReceiverAsync(options);
        Assert.True(await SecondsUntilCloseAsync(port, IntegrationV1.Preamble(IntegrationV1.Mode.Connect)) > 0.25);
        Assert.True(await SecondsUntilCloseAsync(port, IntegrationV1.Preamble(IntegrationV1.Mode.Connect)) < 0.25);
    }

    [Fact]
    public async Task APenaltyDoesNotKeepOutAGoodDeviceAtTheSameAddress()
    {
        var options = FastOptions();
        options.AddressPenaltyBase = TimeSpan.FromSeconds(30);
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        // Another program at this address keeps failing.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var failing = await RawConnectAsync(port);
            await failing.SendAsync(Encoding.ASCII.GetBytes("garbage"));
            await BytesUntilCloseAsync(failing);
        }
        await device.Client.PingAsync();
    }

    [Fact]
    public async Task ConnectingAndLeavingPromptlyCostsNothing()
    {
        var options = FastOptions();
        options.AddressPenaltyBase = TimeSpan.FromSeconds(30);
        options.HandshakeTimeout = TimeSpan.FromSeconds(1);
        options.PenalisedHandshakeTimeout = TimeSpan.FromSeconds(0.05);
        var (_, port) = await StartReceiverAsync(options);
        var first = await RawConnectAsync(port);
        first.Cancel();
        await Task.Delay(100);
        // The ordinary deadline applies, not the penalised one.
        Assert.True(await SecondsUntilCloseAsync(port, Array.Empty<byte>()) > 0.5);
    }

    [Fact]
    public async Task LeavingAfterSendingPartOfAPreambleIsPenalised()
    {
        var options = FastOptions();
        options.AddressPenaltyBase = TimeSpan.FromSeconds(30);
        options.HandshakeTimeout = TimeSpan.FromSeconds(1);
        options.PenalisedHandshakeTimeout = TimeSpan.FromSeconds(0.05);
        var (_, port) = await StartReceiverAsync(options);
        var first = await RawConnectAsync(port);
        await first.SendAsync(Encoding.ASCII.GetBytes("EI"));
        await Task.Delay(50);
        first.Cancel();
        await Task.Delay(100);
        Assert.True(await SecondsUntilCloseAsync(port, Array.Empty<byte>()) < 0.5);
    }

    [Fact]
    public async Task IdleConnectionIsClosed()
    {
        var options = FastOptions();
        options.IdleTimeout = TimeSpan.FromSeconds(0.2);
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        var started = DateTime.UtcNow;
        await BytesUntilCloseAsync(raw.Stream, 3);
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 1);
    }

    [Fact]
    public async Task SlowHandshakeIsDroppedAtTheDeadline()
    {
        var options = FastOptions();
        options.HandshakeTimeout = TimeSpan.FromSeconds(0.2);
        var (_, port) = await StartReceiverAsync(options);
        var stream = await RawConnectAsync(port);
        var started = DateTime.UtcNow;
        Assert.Equal(0, await BytesUntilCloseAsync(stream, 3));
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 2);
    }

    [Fact]
    public async Task OversizedFrameBeforeAuthenticationIsRefused()
    {
        var (_, port) = await StartReceiverAsync();
        var stream = await RawConnectAsync(port);
        await stream.SendAsync(IntegrationV1.Preamble(IntegrationV1.Mode.Connect).Concat(new byte[] { 0xFF, 0xFF }).ToArray());
        Assert.Equal(0, await BytesUntilCloseAsync(stream));
    }

    // Limits

    [Fact]
    public async Task ItemLimit()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { Items = 3 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        for (var index = 0; index < 3; index++)
        {
            await device.Client.PutPinAsync($"pin-{index}", Palace);
        }
        await ExpectRejectedAsync(ErrorCode.LimitItems, () => device.Client.PutPinAsync("one-too-many", Palace));
        // Replacing an existing item is still fine.
        await device.Client.PutPinAsync("pin-0", CityHall);
    }

    [Fact]
    public async Task PhotoLimit()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { Photos = 1 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.PutPhotoAsync("p1", Palace, TestImages.Jpeg);
        await ExpectRejectedAsync(ErrorCode.LimitPhotos, () => device.Client.PutPhotoAsync("p2", Palace, TestImages.Jpeg));
    }

    [Fact]
    public async Task OpenTrackLimit()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { OpenTracks = 1 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var point = new[] { new TrackPoint(Palace) };
        await device.Client.AppendTrackAsync("one", point);
        await ExpectRejectedAsync(ErrorCode.LimitTracks, () => device.Client.AppendTrackAsync("two", point));
        await device.Client.FinishTrackAsync("one");
        await device.Client.AppendTrackAsync("two", point);
    }

    [Fact]
    public async Task FinishedTrackCannotGrow()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var point = new[] { new TrackPoint(Palace) };
        await device.Client.AppendTrackAsync("walk", point);
        await device.Client.FinishTrackAsync("walk");
        await ExpectRejectedAsync(ErrorCode.InvalidState, () => device.Client.AppendTrackAsync("walk", point));
    }

    [Fact]
    public async Task TrackDropsOldestPointsWhenFull()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { TrackPoints = 10 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.AppendTrackAsync("walk", Enumerable.Range(0, 25).Select(index => new TrackPoint(Palace, index)).ToArray());
        var stored = receiver.Items(device.Identity.Fingerprint).First().Points;
        Assert.Equal(10, stored.Count);
        Assert.Equal(15, stored[0].TimestampMs);
        Assert.Equal(24, stored[^1].TimestampMs);
    }

    [Fact]
    public async Task DiskLimit()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { DiskBytes = 1500 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var text = new string('x', 900);
        await device.Client.PutNoteAsync("a", Palace, text);
        await ExpectRejectedAsync(ErrorCode.LimitDisk, () => device.Client.PutNoteAsync("b", Palace, text));
        await device.Client.DeleteAsync("a");
        await device.Client.PutNoteAsync("b", Palace, text);
    }

    [Fact]
    public async Task KindConflict()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.PutPinAsync("x", Palace);
        await ExpectRejectedAsync(ErrorCode.KindConflict, () => device.Client.PutMarkerAsync("x", Palace));
    }

    [Fact]
    public async Task MessageRateLimitDoesNotUseUpTheNumber()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { MessagesPerSecond = 1, MessageBurst = 1 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        Assert.Equal(1UL, (await raw.SendAsync("{\"t\":\"ping\",\"seq\":1}")).Ack);
        var limited = await raw.SendAsync("{\"t\":\"ping\",\"seq\":2}");
        Assert.Equal(ErrorCode.RateLimited.ToWire(), limited.Code);
        Assert.True(limited.RetryAfterMs > 0);
        await Task.Delay(1100);
        var retried = await raw.SendAsync("{\"t\":\"ping\",\"seq\":2}");
        Assert.Equal(2UL, retried.Ack);
        Assert.False(retried.Duplicate);
    }

    [Fact]
    public async Task SdkPacesItselfBelowTheRateLimit()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { MessagesPerSecond = 50, MessageBurst = 2 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var started = DateTime.UtcNow;
        for (var index = 0; index < 12; index++)
        {
            await device.Client.PingAsync();
        }
        Assert.True((DateTime.UtcNow - started).TotalSeconds > 0.15);
        Assert.Equal(12UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task RepeatedRejectionsDisconnectAndPenalise()
    {
        var options = FastOptions();
        options.MaxRejectionsPerMinute = 3;
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        RawReply? last = null;
        for (var index = 0; index < 4; index++)
        {
            last = await raw.SendAsync("{\"t\":\"nonsense\",\"seq\":1}");
        }
        Assert.Equal(ErrorCode.TooManyErrors.ToWire(), last!.Code);
        Assert.True(last.Fatal);
        // The device should be ignored for now.
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => device.Client.PingAsync());
    }

    [Fact]
    public async Task MalformedMessagesChangeNothing()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        var file = VectorFiles.Load("spec/vectors/integration-v1/messages.json");
        foreach (var entry in file.GetProperty("invalid").EnumerateArray())
        {
            var reply = await raw.SendAsync(entry.GetProperty("json").GetString()!);
            Assert.True(entry.GetProperty("code").GetString() == reply.Code, entry.GetProperty("name").GetString());
        }
        Assert.Empty(receiver.Items(device.Identity.Fingerprint));
        // A message that does not parse must not use up a sequence number.
        Assert.Equal(0UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    // Fixes found by review of the Swift SDK

    [Fact]
    public async Task ReplayedPairingFirstMessageGetsNothing()
    {
        var (receiver, port) = await StartReceiverAsync();
        var ticket = receiver.OpenPairingWindow();
        var captured = PairingFirstMessage(ticket.Code, out _);

        var original = await RawConnectAsync(port);
        await original.SendAsync(captured);
        Assert.NotEmpty(await original.ReadFrameAsync());
        original.Cancel();

        var replay = await RawConnectAsync(port);
        await replay.SendAsync(captured);
        // Without the code, a recording must not get an answer.
        Assert.Equal(0, await BytesUntilCloseAsync(replay));
    }

    [Fact]
    public async Task PairingAgainDoesNotMakeOldRecordingsWorkAgain()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var (_, recording) = ConnectMessage(device.Identity.PrivateKey, receiver.PublicKey, 5000);

        var first = await RawConnectAsync(port);
        await first.SendAsync(recording);
        await first.ReadFrameAsync();
        first.Cancel();

        receiver.Revoke(device.Identity.Fingerprint);
        var again = await PairAsync(port, receiver.OpenPairingWindow(), device.Identity, "Back again");
        Assert.Equal(5000UL, again.CounterFloor);

        var replay = await RawConnectAsync(port);
        await replay.SendAsync(recording);
        Assert.Equal(0, await BytesUntilCloseAsync(replay));
    }

    /// <summary>A store that has lost its count and whose clock is wrong, as after restoring an old backup.</summary>
    private sealed class ForgetfulClockStore : IDeviceStore
    {
        private readonly InMemoryDeviceStore inner = new();

        public DeviceIdentity LoadOrCreateIdentity() => inner.LoadOrCreateIdentity();

        public IReadOnlyList<ReceiverPairing> Pairings() => inner.Pairings();

        public void Save(ReceiverPairing pairing) => inner.Save(pairing);

        public void UpdateAddress(string receiverFingerprint, string host, ushort port) => inner.UpdateAddress(receiverFingerprint, host, port);

        public void RemovePairing(string fingerprint) => inner.RemovePairing(fingerprint);

        public ulong NextConnectCounter(string receiverFingerprint, ulong nowMs) => inner.NextConnectCounter(receiverFingerprint, 1);
    }

    [Fact]
    public async Task PairingAgainLetsADeviceThatLostItsCounterBackIn()
    {
        var (receiver, port) = await StartReceiverAsync();
        var healthy = await PairedDeviceAsync(receiver, port);
        await healthy.Client.PingAsync();
        healthy.Client.Close();

        // The same key, in a store with no memory and a clock stuck in 1970.
        var store = new ForgetfulClockStore();
        var stale = new ReceiverPairing("x", healthy.Pairing.ReceiverKey, new[] { ItemKind.Pin });
        var locked = new ErdigoClient(healthy.Identity, stale, store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(0.5));
        // A counter below the stored one must get no answer.
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => locked.PingAsync());

        var pairing = await PairAsync(port, receiver.OpenPairingWindow(), healthy.Identity, "Back");
        Assert.True(pairing.CounterFloor > 1);
        store.Save(pairing);
        var restored = new ErdigoClient(healthy.Identity, pairing, store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(1));
        await restored.PingAsync();
    }

    [Fact]
    public async Task BeingToldToSlowDownIsNotPunished()
    {
        var options = FastOptions();
        options.MaxRejectionsPerMinute = 2;
        options.Limits = options.Limits with { MessagesPerSecond = 40, MessageBurst = 2 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);

        // Each new client starts with a full allowance of its own, while the receiver's has run down.
        for (var round = 0; round < 4; round++)
        {
            var client = new ErdigoClient(device.Identity, device.Pairing, device.Store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(1));
            for (var index = 0; index < 4; index++)
            {
                await client.PingAsync();
            }
            client.Close();
        }
        Assert.Equal(16UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    /// <summary>A long wait for track points must not make waits for ordinary messages look ignored.</summary>
    [Fact]
    public async Task ObeyingEveryAnswerIsNeverPunished()
    {
        var options = FastOptions();
        options.MaxRejectionsPerMinute = 2;
        options.Limits = options.Limits with { MessagesPerSecond = 50, MessageBurst = 1, PointsPerSecond = 1, PointBurst = 100 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        var points = string.Join(",", Enumerable.Repeat("{\"lat\":1,\"lon\":1}", 100));

        var seq = 1UL;
        async Task SendObeyingAsync(Func<ulong, string> json)
        {
            while (true)
            {
                var reply = await raw.SendAsync(json(seq));
                if (reply.RetryAfterMs is { } wait && reply.Code == ErrorCode.RateLimited.ToWire())
                {
                    // Point waits are too long for a test, so those are skipped.
                    if (wait > 1000)
                    {
                        return;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(wait));
                    continue;
                }
                Assert.Null(reply.Code);
                seq += 1;
                return;
            }
        }
        await SendObeyingAsync(number => $"{{\"t\":\"track_append\",\"seq\":{number},\"id\":\"w\",\"points\":[{points}]}}");
        await SendObeyingAsync(number => $"{{\"t\":\"track_append\",\"seq\":{number},\"id\":\"w\",\"points\":[{points}]}}");
        for (var index = 0; index < 20; index++)
        {
            await SendObeyingAsync(number => $"{{\"t\":\"ping\",\"seq\":{number}}}");
        }
        Assert.Equal(21UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task SendingJustInsideTheSlackDoesNotAvoidPunishment()
    {
        var options = FastOptions();
        options.MaxRejectionsPerMinute = 3;
        options.Limits = options.Limits with { MessagesPerSecond = 1, MessageBurst = 1 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        await raw.SendAsync("{\"t\":\"ping\",\"seq\":1}");
        var told = await raw.SendAsync("{\"t\":\"ping\",\"seq\":2}");
        var wait = told.RetryAfterMs!.Value;
        // Arrive just before the time given, then keep going as fast as answers return.
        await Task.Delay(TimeSpan.FromMilliseconds(wait - 30));
        RawReply? last = null;
        for (var index = 0; index < 8; index++)
        {
            last = await raw.SendAsync("{\"t\":\"ping\",\"seq\":2}");
            if (last.Fatal == true)
            {
                break;
            }
        }
        Assert.Equal(ErrorCode.TooManyErrors.ToWire(), last!.Code);
    }

    [Fact]
    public async Task IgnoringTheRateLimitIsPunished()
    {
        var options = FastOptions();
        options.MaxRejectionsPerMinute = 3;
        options.Limits = options.Limits with { MessagesPerSecond = 1, MessageBurst = 1 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        RawReply? last = null;
        for (var seq = 1; seq <= 8; seq++)
        {
            last = await raw.SendAsync($"{{\"t\":\"ping\",\"seq\":{seq}}}");
            if (last.Fatal == true)
            {
                break;
            }
        }
        Assert.Equal(ErrorCode.TooManyErrors.ToWire(), last!.Code);
    }

    [Fact]
    public async Task PointRateLimit()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { PointsPerSecond = 1, PointBurst = 100 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        const string point = "{\"lat\":1,\"lon\":1}";
        var points = string.Join(",", Enumerable.Repeat(point, 100));
        Assert.Equal(1UL, (await raw.SendAsync($"{{\"t\":\"track_append\",\"seq\":1,\"id\":\"w\",\"points\":[{points}]}}")).Ack);
        Assert.Equal(ErrorCode.RateLimited.ToWire(), (await raw.SendAsync($"{{\"t\":\"track_append\",\"seq\":2,\"id\":\"w\",\"points\":[{point}]}}")).Code);
    }

    [Fact]
    public async Task TotalTrackLimit()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { Tracks = 2 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        var point = new[] { new TrackPoint(Palace) };
        foreach (var name in new[] { "one", "two" })
        {
            await device.Client.AppendTrackAsync(name, point);
            await device.Client.FinishTrackAsync(name);
        }
        await ExpectRejectedAsync(ErrorCode.LimitTracks, () => device.Client.AppendTrackAsync("three", point));
        await device.Client.DeleteAsync("one");
        await device.Client.AppendTrackAsync("three", point);
    }

    [Fact]
    public async Task TotalStorageLimit()
    {
        var options = FastOptions();
        options.DiskBytesTotal = 1500;
        var (receiver, port) = await StartReceiverAsync(options);
        var first = await PairedDeviceAsync(receiver, port);
        var second = await PairedDeviceAsync(receiver, port);
        var text = new string('x', 900);
        await first.Client.PutNoteAsync("a", Palace, text);
        await ExpectRejectedAsync(ErrorCode.LimitDisk, () => second.Client.PutNoteAsync("b", Palace, text));
    }

    [Fact]
    public async Task AnnouncedLowerTextLimitsAreEnforced()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { NoteBytes = 10, TitleScalars = 5 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);
        await ExpectRejectedAsync(ErrorCode.TooLarge, () => device.Client.PutNoteAsync("n", Palace, "more than ten bytes"));
        await ExpectRejectedAsync(ErrorCode.TooLarge, () => device.Client.PutPinAsync("p", Palace, "too long"));
    }

    [Fact]
    public async Task AppendTrackIsNotOvertakenByALaterRequest()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var points = Enumerable.Range(0, 250).Select(index => new TrackPoint(Palace, index)).ToArray();
        var append = device.Client.AppendTrackAsync("w", points);
        await Task.Delay(1);
        await device.Client.FinishTrackAsync("w");
        await append;
        var walk = receiver.Items(device.Identity.Fingerprint).First();
        Assert.Equal(250, walk.Points.Count);
        Assert.True(walk.IsFinished);
    }

    [Fact]
    public async Task UpToUsesAllTheTimeItWasGiven()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.Stop();
        var store = new InMemoryDeviceStore();
        var pairing = new ReceiverPairing("x", receiver.PublicKey, new[] { ItemKind.Pin });
        var client = new ErdigoClient(store.LoadOrCreateIdentity(), pairing, store, Local(port), RetryPolicy.UpTo(TimeSpan.FromSeconds(3)), TimeSpan.FromSeconds(0.2));
        var started = DateTime.UtcNow;
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => client.PingAsync());
        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        Assert.InRange(elapsed, 2.7, 5);
    }

    [Fact]
    public async Task AResendDoesNotWaitForItsPointsAgain()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { PointsPerSecond = 1, PointBurst = 100 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.UpTo(TimeSpan.FromSeconds(5)));
        var point = new TrackPoint(Palace);
        await device.Client.AppendTrackAsync("w", Enumerable.Repeat(point, 95).ToArray());

        // The client now has enough allowance for 5 more, and the connection breaks.
        receiver.SetPaused(true);
        receiver.SetPaused(false);
        var started = DateTime.UtcNow;
        await device.Client.AppendTrackAsync("w", Enumerable.Repeat(point, 5).ToArray());
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 3);
    }

    [Fact]
    public async Task CancellingPairingThrowsCancellation()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.SetApprovalHandler(async (_, _) =>
        {
            await Task.Delay(10_000);
            return Receiver.ApprovalDecision.Deny;
        });
        var ticket = receiver.OpenPairingWindow();
        using var cancel = new CancellationTokenSource();
        var pairing = Pairing.PairAsync(Local(port), ticket, DeviceIdentity.Generate(), "D", new[] { ItemKind.Pin }, handshakeTimeout: TimeSpan.FromSeconds(1), cancellationToken: cancel.Token);
        await Task.Delay(200);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pairing);
    }

    [Fact]
    public async Task CancellingAQueuedRequestReturnsPromptly()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.Stop();
        var store = new InMemoryDeviceStore();
        var pairing = new ReceiverPairing("x", receiver.PublicKey, new[] { ItemKind.Pin });
        var client = new ErdigoClient(store.LoadOrCreateIdentity(), pairing, store, Local(port), RetryPolicy.Forever, TimeSpan.FromSeconds(0.2));

        using var runningCancel = new CancellationTokenSource();
        using var queuedCancel = new CancellationTokenSource();
        var running = client.PingAsync(runningCancel.Token);
        await Task.Delay(100);
        var queued = client.PingAsync(queuedCancel.Token);
        await Task.Delay(100);

        var started = DateTime.UtcNow;
        queuedCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 0.5);
        runningCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RetryBudgetCountsFromTheFailureNotFromTheRequest()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { PointsPerSecond = 25, PointBurst = 100 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.UpTo(TimeSpan.FromSeconds(2)));
        var points = Enumerable.Range(0, 200).Select(index => new TrackPoint(Palace, index)).ToArray();

        var append = device.Client.AppendTrackAsync("w", points);
        // The second batch waits about four seconds for its allowance, longer than the two allowed for retrying.
        await Task.Delay(1000);
        receiver.SetPaused(true);
        receiver.SetPaused(false);
        await append;
        Assert.Equal(200, receiver.Items(device.Identity.Fingerprint).First().Points.Count);
    }

    [Fact]
    public async Task ApprovalTimeoutFiresEvenIfTheHandlerNeverAnswers()
    {
        var options = FastOptions();
        options.ApprovalTimeout = TimeSpan.FromSeconds(0.2);
        var (receiver, port) = await StartReceiverAsync(options);
        // Ignores cancellation completely, as a dialog would.
        var stuck = new TaskCompletionSource<Receiver.ApprovalDecision>();
        receiver.SetApprovalHandler((_, _) => stuck.Task);
        var ticket = receiver.OpenPairingWindow();
        var started = DateTime.UtcNow;
        await ExpectAsync(ErdigoErrorKind.PairingDenied, () => PairAsync(port, ticket, name: "Waiting"));
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 1.5);
        stuck.SetResult(Receiver.ApprovalDecision.Deny);
    }

    [Fact]
    public async Task PauseDeniesAPendingPairing()
    {
        var (receiver, port) = await StartReceiverAsync();
        receiver.SetApprovalHandler(async (request, _) =>
        {
            await Task.Delay(300);
            return Receiver.ApprovalDecision.Allow(request.RequestedKinds);
        });
        var ticket = receiver.OpenPairingWindow();
        var attempt = TryPairAsync(port, ticket);
        await Task.Delay(100);
        receiver.SetPaused(true);
        Assert.False(await attempt);
        // Nothing may be added while paused.
        Assert.Empty(receiver.PairedDevices());
    }

    [Fact]
    public async Task PauseEndsHandshakesInProgress()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var (_, message) = ConnectMessage(device.Identity.PrivateKey, receiver.PublicKey, 77);

        // Only the preamble so far. The rest arrives after the pause.
        var stream = await RawConnectAsync(port);
        await stream.SendAsync(IntegrationV1.Preamble(IntegrationV1.Mode.Connect));
        await Task.Delay(100);
        receiver.SetPaused(true);
        try
        {
            await stream.SendAsync(message.AsMemory(5));
        }
        catch (IOException)
        {
        }
        Assert.Equal(0, await BytesUntilCloseAsync(stream));
    }

    [Fact]
    public async Task HandshakeHeldOpenCannotFinishDuringAPenalty()
    {
        var options = FastOptions();
        options.MaxRejectionsPerMinute = 1;
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port);

        // Connection B gets as far as the handshake reply, and holds back its hello.
        var (handshake, message) = ConnectMessage(device.Identity.PrivateKey, receiver.PublicKey, NextRawCounter());
        var held = await RawConnectAsync(port);
        await held.SendAsync(message);
        handshake.ReadMessage(await held.ReadFrameAsync());
        var heldTransport = handshake.Split();

        // Connection A earns the penalty.
        var raw = await AuthenticatedAsync(device.Identity, receiver.PublicKey, port);
        RawReply? last = null;
        for (var index = 0; index < 3; index++)
        {
            last = await raw.SendAsync("{\"t\":\"nonsense\",\"seq\":1}");
            if (last.Fatal == true)
            {
                break;
            }
        }
        Assert.Equal(ErrorCode.TooManyErrors.ToWire(), last!.Code);

        await held.SendFrameAsync(heldTransport.Seal(Wire.Encode(new Hello(new[] { 1 }, "test/1"))));
        Assert.Equal(0, await BytesUntilCloseAsync(held));
    }

    [Fact]
    public async Task RetainedItemsCanBeDeletedLater()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.PutPinAsync("kept", Palace);
        receiver.Revoke(device.Identity.Fingerprint);
        Assert.Single(receiver.Items(device.Identity.Fingerprint));
        receiver.Revoke(device.Identity.Fingerprint, deleteItems: true);
        Assert.Empty(receiver.Items(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task ForgottenPairingIsNotBroughtBackByARunningClient()
    {
        var (receiver, port) = await StartReceiverAsync();
        var store = new InMemoryDeviceStore();
        var identity = store.LoadOrCreateIdentity();
        var pairing = await PairAsync(port, receiver.OpenPairingWindow(), identity, "D");
        // An out-of-date address makes the client want to save the right one.
        pairing = pairing with { LastHost = "192.0.2.1", LastPort = 1 };
        var client = new ErdigoClient(identity, pairing, store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(1));
        await client.PingAsync();
        Assert.Empty(store.Pairings());
    }

    [Fact]
    public async Task ClosingAClientClosesItsConnection()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var fingerprint = device.Identity.Fingerprint;
        using (var scoped = new ErdigoClient(device.Identity, device.Pairing, device.Store, Local(port), RetryPolicy.None, TimeSpan.FromSeconds(1)))
        {
            await scoped.PingAsync();
        }
        await Task.Delay(500);
        Assert.Contains(new Receiver.Event.Disconnected(fingerprint), receiver.Events);
    }

    [Fact]
    public async Task TheClientRemembersWhereItFoundTheReceiver()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        Assert.Equal("127.0.0.1", device.Pairing.LastHost);
        Assert.Equal(port, device.Pairing.LastPort);
        // With discovery, the remembered address is tried first and is enough.
        var client = new ErdigoClient(device.Identity, device.Pairing, device.Store, ReceiverTarget.Discover, RetryPolicy.None, TimeSpan.FromSeconds(1));
        await client.PingAsync();
        Assert.Equal(new[] { ItemKind.Pin, ItemKind.Marker, ItemKind.Note, ItemKind.Photo, ItemKind.Track }, client.AllowedKinds);
        Assert.Equal(Limits.V1.Items, client.Limits!.Items);
    }
}
