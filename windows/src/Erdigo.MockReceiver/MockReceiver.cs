using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;

namespace Erdigo.MockReceiver;

/// <summary>
/// A stand-in for the receiver side of integration-v1, for developing and testing without the Erdigo app.
/// </summary>
/// <remarks>
/// It enforces the rules in the specification, with these differences from a real receiver: it keeps everything in
/// memory and draws no map, unless <see cref="SetApprovalHandler"/> is used it allows every pairing request without
/// asking anyone, and it checks photos by their structure rather than decoding them (see <c>PhotoPipeline</c>). By
/// default it listens on the loopback interface only.
/// </remarks>
public sealed class MockReceiver : IDisposable
{
    public sealed class Options
    {
        /// <summary>0 asks the system for a free port.</summary>
        public ushort Port { get; set; }

        /// <summary>Accept connections from the local network instead of this computer only.</summary>
        public bool AcceptsLocalNetwork { get; set; }

        /// <summary>Advertise over multicast DNS with a random name that changes every hour.</summary>
        public bool Advertises { get; set; }

        public Limits Limits { get; set; } = Limits.V1;

        public IReadOnlyList<int> AcceptedVersions { get; set; } = new[] { IntegrationV1.Version };

        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(300);

        /// <summary>How long a peer may take to accept an answer before it is disconnected.</summary>
        public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan PairingWindow { get; set; } = TimeSpan.FromSeconds(600);

        public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>How long to hold a connection before closing it without a reply, at least.</summary>
        public TimeSpan SilentDelayMin { get; set; } = TimeSpan.FromSeconds(0.2);

        /// <summary>How long to hold a connection before closing it without a reply, at most.</summary>
        public TimeSpan SilentDelayMax { get; set; } = TimeSpan.FromSeconds(1.0);

        /// <summary>How long an address stays under a penalty after its first failed attempt. Doubles up to 60 seconds.</summary>
        public TimeSpan AddressPenaltyBase { get; set; } = TimeSpan.FromSeconds(0.5);

        /// <summary>How long an address under a penalty has to finish a handshake.</summary>
        public TimeSpan PenalisedHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(1);

        public int MaxPreAuthConnections { get; set; } = 8;

        public int MaxPreAuthConnectionsPerAddress { get; set; } = 2;

        public long DiskBytesTotal { get; set; } = 209_715_200;

        /// <summary>Rejected messages tolerated in one minute before the device is disconnected.</summary>
        public int MaxRejectionsPerMinute { get; set; } = 20;

        /// <summary>A fixed private key for the receiver, for tests. A new one is made otherwise.</summary>
        public byte[]? ReceiverKey { get; set; }

        internal Options Copy() => (Options)MemberwiseClone();
    }

    public sealed record ApprovalRequest(
        string DeviceName,
        string DeviceFingerprint,
        IReadOnlyList<ItemKind> RequestedKinds,
        string? Address,
        bool WasRemovedBefore);

    public abstract class ApprovalDecision
    {
        private ApprovalDecision()
        {
        }

        public static ApprovalDecision Deny { get; } = new DenyDecision();

        /// <summary>Allows the kinds given, which are cut to those requested. A name, if given, replaces the device's own.</summary>
        public static ApprovalDecision Allow(IEnumerable<ItemKind> kinds, string? name = null) => new AllowDecision(kinds.ToArray(), name);

        public sealed class AllowDecision : ApprovalDecision
        {
            internal AllowDecision(IReadOnlyList<ItemKind> kinds, string? name)
            {
                Kinds = kinds;
                Name = name;
            }

            public IReadOnlyList<ItemKind> Kinds { get; }

            public string? Name { get; }
        }

        public sealed class DenyDecision : ApprovalDecision
        {
            internal DenyDecision()
            {
            }
        }
    }

    public abstract record Event
    {
        private Event()
        {
        }

        public sealed record Listening(ushort Port) : Event;

        public sealed record PairingWindowOpened : Event;

        public sealed record PairingRequested(string Device) : Event;

        public sealed record Paired(string Device, IReadOnlyList<ItemKind> Kinds) : Event
        {
            public bool Equals(Paired? other) => other is not null && Device == other.Device && Kinds.SequenceEqual(other.Kinds);

            public override int GetHashCode() => Device.GetHashCode(StringComparison.Ordinal);
        }

        public sealed record PairingDenied(string Device) : Event;

        public sealed record CodeReusedAlarm(string FirstDevice) : Event;

        public sealed record SilentlyDropped(string Reason) : Event;

        public sealed record Connected(string Device) : Event;

        public sealed record Disconnected(string Device) : Event;

        public sealed record Applied(string Device, ulong Seq, string Summary) : Event;

        public sealed record Rejected(string Device, ulong? Seq, string Code) : Event;

        public sealed record Revoked(string Device) : Event;
    }

    public sealed record Item(string Id, ItemKind Kind)
    {
        public double? Latitude { get; init; }

        public double? Longitude { get; init; }

        public string? Title { get; init; }

        public string? Text { get; init; }

        public string? Color { get; init; }

        public byte[]? Photo { get; init; }

        public IReadOnlyList<TrackPoint> Points { get; init; } = Array.Empty<TrackPoint>();

        public bool IsFinished { get; init; }

        public bool Equals(Item? other) =>
            other is not null && Id == other.Id && Kind == other.Kind && Latitude == other.Latitude && Longitude == other.Longitude
            && Title == other.Title && Text == other.Text && Color == other.Color && IsFinished == other.IsFinished
            && (Photo is null ? other.Photo is null : other.Photo is not null && Photo.AsSpan().SequenceEqual(other.Photo))
            && Points.SequenceEqual(other.Points);

        public override int GetHashCode() => HashCode.Combine(Id, Kind);
    }

    public sealed record PairedDevice(string Name, string Fingerprint, IReadOnlyList<ItemKind> Kinds);

    private sealed class Upload
    {
        public Upload(PhotoHeader header) => Header = header;

        public PhotoHeader Header { get; }

        public MemoryStream Received { get; } = new();

        public int NextIndex { get; set; }
    }

    /// <summary>
    /// An authenticated connection. The transport lives here so the receiver can also send a closing message from
    /// outside the connection's own task. Sealing and sending happen together under <see cref="SendLock"/>, so frames
    /// go out in the order their nonces were used.
    /// </summary>
    private sealed class LiveConnection
    {
        public LiveConnection(ByteStream stream, NoiseTransport transport)
        {
            Stream = stream;
            Transport = transport;
        }

        public ByteStream Stream { get; }

        public NoiseTransport Transport { get; }

        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    /// <summary>What a device was last told about waiting, so the receiver can tell whether it listened.</summary>
    private sealed class RetryAdvice
    {
        private double retryAt;
        private bool usedSlack;

        /// <summary>
        /// A device may arrive a little early, because neither its clock nor the network is exact. It uses that up
        /// until it next arrives on time. Anything earlier, or more often, did not wait.
        /// </summary>
        public bool ArrivalCameTooSoon(double now, double wait)
        {
            bool tooSoon;
            if (now >= retryAt)
            {
                tooSoon = false;
                usedSlack = false;
            }
            else if (now >= retryAt - 0.05 && !usedSlack)
            {
                tooSoon = false;
                usedSlack = true;
            }
            else
            {
                tooSoon = true;
            }
            retryAt = now + wait;
            return tooSoon;
        }
    }

    private sealed class Device
    {
        public Device(byte[] key, string name, IReadOnlyList<ItemKind> kinds, Limits limits, double now)
        {
            Key = key;
            KeyText = Hex.Encode(key);
            Fingerprint = IntegrationV1.Fingerprint(key);
            Name = name;
            Kinds = kinds;
            MessageBucket = new TokenBucket(limits.MessageBurst, limits.MessagesPerSecond, now);
            PointBucket = new TokenBucket(limits.PointBurst, limits.PointsPerSecond, now);
        }

        public byte[] Key { get; }

        public string KeyText { get; }

        public string Fingerprint { get; }

        public string Name { get; set; }

        public IReadOnlyList<ItemKind> Kinds { get; set; }

        public ulong LastCounter { get; set; }

        public ulong LastSeq { get; set; }

        public byte[]? LastDigest { get; set; }

        /// <summary>Null after a request that succeeded, otherwise the error it got.</summary>
        public ReceiverError? LastFailure { get; set; }

        public Dictionary<string, Item> Items { get; set; } = new(StringComparer.Ordinal);

        public Upload? Upload { get; set; }

        public long DiskBytes { get; set; }

        public TokenBucket MessageBucket { get; }

        public TokenBucket PointBucket { get; }

        public RetryAdvice MessageRetry { get; } = new();

        public RetryAdvice PointRetry { get; } = new();

        public List<double> Rejections { get; set; } = new();

        public double IgnoredUntil { get; set; }

        public double NextPenalty { get; set; } = 60;

        public LiveConnection? Connection { get; set; }
    }

    private abstract class WindowUse
    {
        public sealed class Unused : WindowUse
        {
        }

        public sealed class Pending : WindowUse
        {
            public Pending(string key, Task<PairStatus> status, TaskCompletionSource<ApprovalDecision> race)
            {
                Key = key;
                Status = status;
                Race = race;
            }

            public string Key { get; }

            public Task<PairStatus> Status { get; }

            /// <summary>Whichever comes first decides: the person, the timer, or a pause or alarm.</summary>
            public TaskCompletionSource<ApprovalDecision> Race { get; }
        }

        public sealed class Approved : WindowUse
        {
            public Approved(string key, IReadOnlyList<ItemKind> kinds)
            {
                Key = key;
                Kinds = kinds;
            }

            public string Key { get; }

            public IReadOnlyList<ItemKind> Kinds { get; }
        }

        public sealed class Denied : WindowUse
        {
            public Denied(string key) => Key = key;

            public string Key { get; }
        }

        public sealed class Burned : WindowUse
        {
        }
    }

    private sealed class PairingWindowState
    {
        public PairingWindowState(PairingCode code, double expiresAt)
        {
            Code = code;
            ExpiresAt = expiresAt;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public PairingCode Code { get; }

        public double ExpiresAt { get; }

        public WindowUse Use { get; set; } = new WindowUse.Unused();

        public HashSet<string> SeenEphemeralKeys { get; } = new(StringComparer.Ordinal);

        public Queue<string> SeenOrder { get; } = new();
    }

    /// <summary>A connection's claim on the limited slots for connections that have not yet proven themselves.</summary>
    private sealed class Admission
    {
        public Admission(string address, bool isPenalised)
        {
            Address = address;
            IsPenalised = isPenalised;
        }

        public string Address { get; }

        public bool IsPenalised { get; }

        public bool IsHeld { get; set; } = true;
    }

    private const int RememberedEphemeralKeys = 4096;

    private readonly Options options;
    private readonly byte[] staticKey;
    private readonly object sync = new();
    private readonly Dictionary<string, Device> devices = new(StringComparer.Ordinal);
    private readonly HashSet<string> removedKeys = new(StringComparer.Ordinal);

    /// <summary>The counters of removed devices, so that old recordings do not become usable again.</summary>
    private readonly Dictionary<string, ulong> removedCounters = new(StringComparer.Ordinal);

    /// <summary>Items of removed devices, kept unless the person asked to delete them.</summary>
    private readonly Dictionary<string, Dictionary<string, Item>> retainedItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> preAuthByAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Until, double Seconds)> addressPenalty = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ByteStream> openStreams = new();
    private readonly SemaphoreSlim photoDecodeGate = new(1, 1);
    private readonly List<Event> events = new();
    private Socket? listener;
    private CancellationTokenSource? running;
    private MdnsAdvertiser? advertiser;
    private ushort boundPort;
    private PairingWindowState? window;
    private bool isPaused;
    private int preAuthTotal;
    private Action<Event>? eventHandler;
    private Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>>? approvalHandler;

    public MockReceiver(Options? options = null)
    {
        this.options = (options ?? new Options()).Copy();
        staticKey = GC.AllocateArray<byte>(Noise.DhLength, pinned: true);
        if (this.options.ReceiverKey is { } raw)
        {
            if (raw.Length != Noise.DhLength)
            {
                throw new ArgumentException("a receiver key is 32 bytes", nameof(options));
            }
            raw.CopyTo(staticKey, 0);
        }
        else
        {
            RandomNumberGenerator.Fill(staticKey);
        }
        PublicKey = Noise.PublicKey(staticKey);
        Fingerprint = IntegrationV1.Fingerprint(PublicKey);
    }

    // Control

    public byte[] PublicKey { get; }

    public string Fingerprint { get; }

    public ushort Port
    {
        get
        {
            lock (sync)
            {
                return boundPort;
            }
        }
    }

    /// <summary>Everything that happened, most recent last. Older entries are dropped after ten thousand.</summary>
    public IReadOnlyList<Event> Events
    {
        get
        {
            lock (sync)
            {
                return events.ToArray();
            }
        }
    }

    /// <summary>
    /// Called for every event, in order. It runs while the receiver holds its lock, so it must not call back into
    /// the receiver; printing is fine.
    /// </summary>
    public void SetEventHandler(Action<Event>? handler)
    {
        lock (sync)
        {
            eventHandler = handler;
        }
    }

    /// <summary>
    /// Stands in for the confirmation dialog. By default every request is allowed as asked. If the handler takes
    /// longer than <see cref="Options.ApprovalTimeout"/>, the answer is no and its cancellation token is cancelled.
    /// </summary>
    public void SetApprovalHandler(Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>>? handler)
    {
        lock (sync)
        {
            approvalHandler = handler;
        }
    }

    /// <summary>Starts listening and returns the port.</summary>
    public Task<ushort> StartAsync()
    {
        Socket socket;
        if (options.AcceptsLocalNetwork && Socket.OSSupportsIPv6)
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
            SocketReuse.AllowRebinding(socket);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, options.Port));
        }
        else
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            SocketReuse.AllowRebinding(socket);
            socket.Bind(new IPEndPoint(options.AcceptsLocalNetwork ? IPAddress.Any : IPAddress.Loopback, options.Port));
        }
        try
        {
            socket.Listen(64);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var port = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
        var stop = new CancellationTokenSource();
        lock (sync)
        {
            listener = socket;
            running = stop;
            boundPort = port;
            Record(new Event.Listening(port));
        }
        _ = AcceptLoopAsync(socket, stop.Token);

        if (options.Advertises)
        {
            var created = new MdnsAdvertiser(port);
            lock (sync)
            {
                advertiser = created;
            }
            created.Start();
            _ = RotateServiceNameAsync(created, stop.Token);
        }
        return Task.FromResult(port);
    }

    private async Task AcceptLoopAsync(Socket socket, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await socket.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }
            var remote = (accepted.RemoteEndPoint as IPEndPoint)?.Address;
            var allowed = remote is not null && (options.AcceptsLocalNetwork ? LocalNetwork.IsLocal(remote) : IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote));
            if (!allowed)
            {
                // Connections from outside the local network are refused before anything is read.
                accepted.Dispose();
                continue;
            }
            var stream = new ByteStream(accepted);
            _ = Task.Run(() => ServeAsync(stream));
        }
    }

    private async Task RotateServiceNameAsync(MdnsAdvertiser created, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            bool paused;
            lock (sync)
            {
                paused = isPaused;
            }
            if (!paused)
            {
                created.Rotate();
            }
        }
    }

    public void Stop()
    {
        List<ByteStream> streams;
        MdnsAdvertiser? stopping;
        lock (sync)
        {
            running?.Cancel();
            running = null;
            listener?.Dispose();
            listener = null;
            stopping = advertiser;
            advertiser = null;
            if (window?.Use is WindowUse.Pending pending)
            {
                pending.Race.TrySetResult(ApprovalDecision.Deny);
            }
            streams = openStreams.Values.ToList();
            openStreams.Clear();
        }
        stopping?.Dispose();
        foreach (var stream in streams)
        {
            stream.Cancel();
        }
    }

    public void Dispose()
    {
        Stop();
        CryptographicOperations.ZeroMemory(staticKey);
    }

    /// <summary>Opens a pairing window and returns its one-time ticket. Any earlier window is closed.</summary>
    public PairingTicket OpenPairingWindow() => OpenPairingWindow(PairingCode.Generate());

    /// <summary>Opens a window with a code chosen by a test, to play a second receiver holding the same code.</summary>
    internal PairingTicket OpenPairingWindow(PairingCode code)
    {
        lock (sync)
        {
            if (window?.Use is WindowUse.Pending pending)
            {
                pending.Race.TrySetResult(ApprovalDecision.Deny);
            }
            window = new PairingWindowState(code, Now() + options.PairingWindow.TotalSeconds);
            Record(new Event.PairingWindowOpened());
            return new PairingTicket(code, Fingerprint);
        }
    }

    public void ClosePairingWindow()
    {
        lock (sync)
        {
            if (window?.Use is WindowUse.Pending pending)
            {
                pending.Race.TrySetResult(ApprovalDecision.Deny);
            }
            window = null;
        }
    }

    /// <summary>Stops all connections at once and answers nothing until <c>SetPaused(false)</c>.</summary>
    public void SetPaused(bool paused)
    {
        var toCancel = new List<ByteStream>();
        MdnsAdvertiser? advertising;
        lock (sync)
        {
            isPaused = paused;
            advertising = advertiser;
            if (paused)
            {
                if (window?.Use is WindowUse.Pending pending)
                {
                    pending.Race.TrySetResult(ApprovalDecision.Deny);
                }
                var authenticated = new HashSet<Guid>();
                foreach (var device in devices.Values)
                {
                    if (device.Connection is { } connection)
                    {
                        device.Connection = null;
                        authenticated.Add(connection.Stream.Identifier);
                        SendThenClose(new ReceiverMessage.Error(new ReceiverError(ErrorCode.Paused, fatal: true)), connection);
                    }
                }
                // Everything still in a handshake simply ends.
                toCancel.AddRange(openStreams.Where(entry => !authenticated.Contains(entry.Key)).Select(entry => entry.Value));
            }
        }
        if (advertising is not null)
        {
            if (paused)
            {
                advertising.Pause();
            }
            else
            {
                advertising.Resume();
            }
        }
        foreach (var stream in toCancel)
        {
            stream.Cancel();
        }
    }

    /// <summary>Sends a last message and closes, even if the peer has stopped reading.</summary>
    private static void SendThenClose(ReceiverMessage message, LiveConnection connection)
    {
        var stream = connection.Stream;
        stream.ArmDeadline(TimeSpan.FromSeconds(2));
        _ = Task.Run(async () =>
        {
            var sent = false;
            try
            {
                await connection.SendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var sealedMessage = connection.Transport.Seal(Wire.Encode(message));
                    await stream.SendFrameAsync(sealedMessage).ConfigureAwait(false);
                    sent = true;
                }
                finally
                {
                    connection.SendLock.Release();
                }
            }
            catch (Exception error) when (error is IOException or NoiseException or ObjectDisposedException)
            {
            }
            finally
            {
                // Once the error is out, end the stream gracefully and let the deadline above close the socket.
                // Closing it at once resets the connection on Windows, which can lose the error before it is read.
                if (sent)
                {
                    stream.FinishSending();
                }
                else
                {
                    stream.Cancel();
                }
            }
        });
    }

    /// <summary>
    /// Removes a device from the list. With <paramref name="deleteItems"/>, also deletes its items, including those
    /// kept from an earlier removal.
    /// </summary>
    public void Revoke(string deviceFingerprint, bool deleteItems = false)
    {
        lock (sync)
        {
            if (devices.Values.FirstOrDefault(device => device.Fingerprint == deviceFingerprint) is { } found)
            {
                Remove(found, deleteItems);
            }
            else if (deleteItems && retainedItems.Keys.FirstOrDefault(key => IntegrationV1.Fingerprint(Hex.Decode(key)!) == deviceFingerprint) is { } key)
            {
                retainedItems.Remove(key);
            }
        }
    }

    private void Remove(Device device, bool deleteItems)
    {
        // No message: a removed device learns only that the connection ended.
        device.Connection?.Stream.Cancel();
        device.Connection = null;
        devices.Remove(device.KeyText);
        removedKeys.Add(device.KeyText);
        removedCounters[device.KeyText] = device.LastCounter;
        if (deleteItems)
        {
            retainedItems.Remove(device.KeyText);
        }
        else
        {
            retainedItems[device.KeyText] = device.Items;
        }
        Record(new Event.Revoked(device.Fingerprint));
    }

    public IReadOnlyList<PairedDevice> PairedDevices()
    {
        lock (sync)
        {
            return devices.Values
                .Select(device => new PairedDevice(device.Name, device.Fingerprint, device.Kinds))
                .OrderBy(device => device.Fingerprint, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<Item> Items(string deviceFingerprint)
    {
        lock (sync)
        {
            if (devices.Values.FirstOrDefault(device => device.Fingerprint == deviceFingerprint) is { } found)
            {
                return found.Items.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            }
            var retained = retainedItems.FirstOrDefault(entry => IntegrationV1.Fingerprint(Hex.Decode(entry.Key)!) == deviceFingerprint).Value;
            return retained?.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray() ?? Array.Empty<Item>();
        }
    }

    public ulong? LastSeq(string deviceFingerprint)
    {
        lock (sync)
        {
            return devices.Values.FirstOrDefault(device => device.Fingerprint == deviceFingerprint)?.LastSeq;
        }
    }

    // Helpers

    private static double Now() => MonotonicClock.Now;

    private static double RandomBetween(double low, double high) =>
        low + (high - low) * (RandomNumberGenerator.GetInt32(0, 1_000_001) / 1_000_000.0);

    private void Record(Event happened)
    {
        events.Add(happened);
        if (events.Count > 10_000)
        {
            events.RemoveRange(0, 1000);
        }
        eventHandler?.Invoke(happened);
    }

    private long TotalDiskBytes =>
        devices.Values.Sum(device => device.DiskBytes) + retainedItems.Values.Sum(items => items.Values.Sum(Cost));

    /// <summary>
    /// Every way a connection can fail before proving itself ends here: the same pause, the same penalty, and nothing
    /// sent. That is what keeps a scanner from learning anything.
    /// </summary>
    private async Task CloseSilentlyAsync(ByteStream stream, string address, string reason)
    {
        lock (sync)
        {
            Record(new Event.SilentlyDropped(reason));
            NotePreAuthFailure(address);
        }
        var delay = RandomBetween(options.SilentDelayMin.TotalSeconds, options.SilentDelayMax.TotalSeconds);
        await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
        stream.Cancel();
    }

    private void NotePreAuthFailure(string address)
    {
        var baseSeconds = options.AddressPenaltyBase.TotalSeconds;
        if (baseSeconds <= 0)
        {
            return;
        }
        var now = Now();
        if (addressPenalty.Count > 1024)
        {
            foreach (var expired in addressPenalty.Where(entry => entry.Value.Until <= now).Select(entry => entry.Key).ToList())
            {
                addressPenalty.Remove(expired);
            }
        }
        var current = addressPenalty.TryGetValue(address, out var penalty) ? penalty.Seconds : baseSeconds;
        addressPenalty[address] = (now + current, Math.Min(current * 2, 60));
    }

    /// <summary>
    /// An address under a penalty still gets read, because a good device may share it with a failing program, and its
    /// first message arrives at once. It just gets one slot and little time.
    /// </summary>
    private Admission? Admit(string address)
    {
        var isPenalised = addressPenalty.TryGetValue(address, out var penalty) && penalty.Until > Now();
        var perAddress = isPenalised ? 1 : options.MaxPreAuthConnectionsPerAddress;
        if (preAuthTotal >= options.MaxPreAuthConnections || preAuthByAddress.GetValueOrDefault(address) >= perAddress)
        {
            return null;
        }
        preAuthTotal += 1;
        preAuthByAddress[address] = preAuthByAddress.GetValueOrDefault(address) + 1;
        return new Admission(address, isPenalised);
    }

    private void LeavePreAuth(Admission admission)
    {
        if (!admission.IsHeld)
        {
            return;
        }
        admission.IsHeld = false;
        preAuthTotal -= 1;
        var remaining = preAuthByAddress.GetValueOrDefault(admission.Address, 1) - 1;
        if (remaining > 0)
        {
            preAuthByAddress[admission.Address] = remaining;
        }
        else
        {
            preAuthByAddress.Remove(admission.Address);
        }
    }

    // Connections

    private async Task ServeAsync(ByteStream stream)
    {
        var streamId = stream.Identifier;
        lock (sync)
        {
            if (running is null)
            {
                stream.Cancel();
                return;
            }
            openStreams[streamId] = stream;
        }
        try
        {
            var address = stream.RemoteAddress?.Host ?? "unknown";
            Admission? admission;
            lock (sync)
            {
                admission = Admit(address);
                if (admission is null)
                {
                    // Beyond the limits nothing is read at all.
                    stream.Cancel();
                    Record(new Event.SilentlyDropped("pre-auth limit"));
                    return;
                }
            }
            try
            {
                await ServeAdmittedAsync(stream, admission, address).ConfigureAwait(false);
            }
            finally
            {
                lock (sync)
                {
                    LeavePreAuth(admission);
                }
            }
        }
        catch (Exception error) when (error is IOException or NoiseException or ObjectDisposedException or WireException or SocketException)
        {
            stream.Cancel();
        }
        finally
        {
            lock (sync)
            {
                openStreams.Remove(streamId);
            }
        }
    }

    private async Task ServeAdmittedAsync(ByteStream stream, Admission admission, string address)
    {
        var handshakeTimeout = options.HandshakeTimeout;
        stream.ArmDeadline(admission.IsPenalised && options.PenalisedHandshakeTimeout < handshakeTimeout ? options.PenalisedHandshakeTimeout : handshakeTimeout);
        var arrivedAt = Now();

        IntegrationV1.Mode mode;
        try
        {
            var parsed = IntegrationV1.ParsePreamble(await stream.ReadExactlyAsync(5).ConfigureAwait(false));
            if (parsed is null)
            {
                await CloseSilentlyAsync(stream, address, "unknown preamble").ConfigureAwait(false);
                return;
            }
            mode = parsed.Value;
        }
        catch (ByteStreamException error)
        {
            // Connecting and promptly leaving, having sent nothing, costs nothing. Staying any longer holds a slot
            // someone else may need, so it counts as a failed attempt.
            if (error.Failure == ByteStreamFailure.Closed && stream.BufferedByteCount == 0 && Now() - arrivedAt < 1)
            {
                stream.Cancel();
                return;
            }
            await CloseSilentlyAsync(stream, address, "incomplete handshake").ConfigureAwait(false);
            return;
        }

        // The state of the receiver is looked at only after the whole first message has arrived or the deadline has
        // passed. A check made any earlier would show in how long the connection stays open.
        byte[] first;
        try
        {
            first = await stream.ReadFrameAsync(IntegrationV1.MaxHandshakeFrameLength).ConfigureAwait(false);
        }
        catch (ByteStreamException)
        {
            await CloseSilentlyAsync(stream, address, "incomplete handshake").ConfigureAwait(false);
            return;
        }
        bool paused;
        lock (sync)
        {
            paused = isPaused;
        }
        if (paused)
        {
            await CloseSilentlyAsync(stream, address, "paused").ConfigureAwait(false);
            return;
        }
        if (admission.IsPenalised)
        {
            stream.ArmDeadline(handshakeTimeout);
        }

        switch (mode)
        {
            case IntegrationV1.Mode.Connect:
                var established = await EstablishConnectionAsync(stream, first, address).ConfigureAwait(false);
                if (established is null)
                {
                    return;
                }
                lock (sync)
                {
                    LeavePreAuth(admission);
                }
                await RunOperationsAsync(established.Value.Connection, established.Value.Device).ConfigureAwait(false);
                break;
            case IntegrationV1.Mode.Pair:
                await RunPairingAsync(stream, first, admission, address).ConfigureAwait(false);
                break;
        }
    }

    private async Task<(Device Device, LiveConnection Connection)?> EstablishConnectionAsync(ByteStream stream, byte[] first, string address)
    {
        var proven = false;
        NoiseHandshake? handshake = null;
        try
        {
            if (first.Length != IntegrationV1.ConnectFirstMessageLength)
            {
                await CloseSilentlyAsync(stream, address, "malformed handshake").ConfigureAwait(false);
                return null;
            }
            handshake = new NoiseHandshake(NoisePattern.IK, isInitiator: false, IntegrationV1.Preamble(IntegrationV1.Mode.Connect), staticKey);
            byte[] payload;
            try
            {
                payload = handshake.ReadMessage(first);
            }
            catch (NoiseException)
            {
                await CloseSilentlyAsync(stream, address, "handshake failed").ConfigureAwait(false);
                return null;
            }

            string? reason = null;
            Device? device = null;
            string keyText = string.Empty;
            lock (sync)
            {
                var key = handshake.RemoteStaticKey;
                keyText = key is null ? string.Empty : Hex.Encode(key);
                if (key is null || !devices.TryGetValue(keyText, out device))
                {
                    reason = "device not allowed";
                }
                else if (payload.Length != 8)
                {
                    reason = "malformed counter";
                }
                else
                {
                    var counter = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(payload);
                    // A replayed first message fails here, so it cannot be used to recognise this receiver.
                    if (counter <= device.LastCounter || counter > IntegrationV1.MaxSequence)
                    {
                        reason = "replayed handshake";
                    }
                    else
                    {
                        device.LastCounter = counter;
                        if (device.IgnoredUntil > Now())
                        {
                            reason = "device penalty";
                        }
                    }
                }
            }
            if (reason is not null || device is null)
            {
                await CloseSilentlyAsync(stream, address, reason ?? "device not allowed").ConfigureAwait(false);
                return null;
            }

            await stream.SendFrameAsync(handshake.WriteMessage()).ConfigureAwait(false);
            var transport = handshake.Split();

            // The hello proves the device holds its key, because a replayed first message cannot produce it. Only now
            // may an older connection be replaced.
            var helloFrame = await stream.ReadFrameAsync().ConfigureAwait(false);
            var hello = Wire.DecodeHello(transport.Open(helloFrame));
            stream.DisarmDeadline();

            LiveConnection? connection = null;
            byte[]? reply = null;
            lock (sync)
            {
                // Things may have changed while waiting for the hello.
                if (!devices.TryGetValue(keyText, out var still) || still != device || isPaused || device.IgnoredUntil > Now())
                {
                    stream.Cancel();
                    return null;
                }
                addressPenalty.Remove(address);
                proven = true;

                var shared = hello.Versions.Where(options.AcceptedVersions.Contains).ToList();
                if (shared.Count == 0)
                {
                    reply = transport.Seal(Wire.Encode(new ReceiverMessage.Error(new ReceiverError(
                        ErrorCode.VersionUnsupported, supported: options.AcceptedVersions.ToArray(), fatal: true))));
                }
                else
                {
                    if (device.Connection is { } previous)
                    {
                        SendThenClose(new ReceiverMessage.Error(new ReceiverError(ErrorCode.Replaced, fatal: true)), previous);
                    }
                    device.Upload = null;
                    connection = new LiveConnection(stream, transport);
                    // Nobody else can hold the new connection's send lock yet, so the welcome is the first frame on it.
                    connection.SendLock.Wait();
                    device.Connection = connection;
                    Record(new Event.Connected(device.Fingerprint));
                    var welcome = new Welcome(shared.Max(), device.LastSeq, device.Kinds, options.Limits);
                    reply = transport.Seal(Wire.Encode(new ReceiverMessage.WelcomeMessage(welcome)));
                }
            }

            if (connection is null)
            {
                await stream.SendFrameAsync(reply).ConfigureAwait(false);
                stream.Cancel();
                return null;
            }
            try
            {
                stream.ArmDeadline(options.SendTimeout);
                await stream.SendFrameAsync(reply).ConfigureAwait(false);
                stream.DisarmDeadline();
            }
            finally
            {
                connection.SendLock.Release();
            }
            return (device, connection);
        }
        catch (Exception error) when (error is IOException or NoiseException or WireException or ObjectDisposedException or SocketException)
        {
            if (!proven)
            {
                lock (sync)
                {
                    NotePreAuthFailure(address);
                }
            }
            stream.Cancel();
            return null;
        }
        finally
        {
            handshake?.Dispose();
        }
    }

    private async Task RunPairingAsync(ByteStream stream, byte[] first, Admission admission, string address)
    {
        NoiseHandshake? handshake = null;
        try
        {
            PairingWindowState? current;
            lock (sync)
            {
                current = window;
            }
            if (current is null || current.ExpiresAt <= Now())
            {
                await CloseSilentlyAsync(stream, address, "no pairing window").ConfigureAwait(false);
                return;
            }
            if (first.Length != IntegrationV1.PairFirstMessageLength)
            {
                await CloseSilentlyAsync(stream, address, "malformed pairing handshake").ConfigureAwait(false);
                return;
            }
            var psk = current.Code.PresharedKey();
            try
            {
                handshake = new NoiseHandshake(NoisePattern.XXpsk0, isInitiator: false, IntegrationV1.Preamble(IntegrationV1.Mode.Pair), staticKey, psk: psk);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(psk);
            }
            try
            {
                handshake.ReadMessage(first);
            }
            catch (NoiseException)
            {
                // The code stays valid, so a stranger cannot spoil it by guessing badly.
                await CloseSilentlyAsync(stream, address, "wrong pairing code").ConfigureAwait(false);
                return;
            }

            // A real attempt always uses a new ephemeral key, so one seen before is a replay.
            var ephemeral = Hex.Encode(first.AsSpan(0, Noise.DhLength));
            bool replayed;
            lock (sync)
            {
                replayed = window?.Id != current.Id || window.SeenEphemeralKeys.Contains(ephemeral);
                if (!replayed)
                {
                    // Oldest forgotten first, so memory stays bounded and nothing ever goes silent for a real device.
                    window!.SeenEphemeralKeys.Add(ephemeral);
                    window.SeenOrder.Enqueue(ephemeral);
                    if (window.SeenOrder.Count > RememberedEphemeralKeys)
                    {
                        window.SeenEphemeralKeys.Remove(window.SeenOrder.Dequeue());
                    }
                }
            }
            if (replayed)
            {
                await CloseSilentlyAsync(stream, address, "replayed handshake").ConfigureAwait(false);
                return;
            }

            await stream.SendFrameAsync(handshake.WriteMessage()).ConfigureAwait(false);
            var third = await stream.ReadFrameAsync(IntegrationV1.MaxHandshakeFrameLength).ConfigureAwait(false);
            var payload = handshake.ReadMessage(third);
            using var transport = handshake.Split();
            // X25519 ignores the top bit of a key, so without this one key could pose as two devices.
            var key = handshake.RemoteStaticKey;
            if (key is null || !Noise.IsCanonicalPublicKey(key))
            {
                stream.Cancel();
                return;
            }
            var request = Wire.DecodePairRequest(payload);
            stream.DisarmDeadline();

            PairingAnswer? answer = null;
            lock (sync)
            {
                if (window?.Id != current.Id || isPaused)
                {
                    stream.Cancel();
                    return;
                }
                addressPenalty.Remove(address);
                // From here the peer has proven it holds the code, and waiting for a person must not use up the slots
                // other connections need.
                LeavePreAuth(admission);
                if (request.Versions.Any(options.AcceptedVersions.Contains))
                {
                    answer = PairingAnswerFor(key, request, address);
                }
            }

            switch (answer)
            {
                case null:
                    await stream.SendFrameAsync(transport.Seal(Wire.Encode(new ReceiverMessage.Error(new ReceiverError(
                        ErrorCode.VersionUnsupported, supported: options.AcceptedVersions.ToArray(), fatal: true))))).ConfigureAwait(false);
                    break;
                case PairingAnswer.Reused:
                    await stream.SendFrameAsync(transport.Seal(Wire.Encode(new ReceiverMessage.Error(new ReceiverError(ErrorCode.CodeReused, fatal: true))))).ConfigureAwait(false);
                    break;
                case PairingAnswer.Final final:
                    await stream.SendFrameAsync(transport.Seal(Wire.Encode(new ReceiverMessage.PairStatusMessage(final.Status)))).ConfigureAwait(false);
                    break;
                case PairingAnswer.Wait wait:
                    await stream.SendFrameAsync(transport.Seal(Wire.Encode(new ReceiverMessage.PairStatusMessage(PairStatus.Pending.Instance)))).ConfigureAwait(false);
                    var decided = await wait.Status.ConfigureAwait(false);
                    await stream.SendFrameAsync(transport.Seal(Wire.Encode(new ReceiverMessage.PairStatusMessage(decided)))).ConfigureAwait(false);
                    break;
            }
            stream.Cancel();
        }
        catch (Exception error) when (error is IOException or NoiseException or WireException or ObjectDisposedException or SocketException)
        {
            lock (sync)
            {
                if (admission.IsHeld)
                {
                    NotePreAuthFailure(address);
                }
            }
            stream.Cancel();
        }
        finally
        {
            handshake?.Dispose();
        }
    }

    /// <summary>The code has been misused: warn the person, undo anything done with it, and stop using it.</summary>
    private void BurnWindow()
    {
        if (window is null)
        {
            return;
        }
        string? firstKey = null;
        switch (window.Use)
        {
            case WindowUse.Pending pending:
                pending.Race.TrySetResult(ApprovalDecision.Deny);
                firstKey = pending.Key;
                break;
            case WindowUse.Approved approved:
                if (devices.TryGetValue(approved.Key, out var firstDevice))
                {
                    Remove(firstDevice, deleteItems: false);
                }
                firstKey = approved.Key;
                break;
            case WindowUse.Denied denied:
                firstKey = denied.Key;
                break;
        }
        window.Use = new WindowUse.Burned();
        Record(new Event.CodeReusedAlarm(firstKey is null ? "none" : IntegrationV1.Fingerprint(Hex.Decode(firstKey)!)));
    }

    private abstract class PairingAnswer
    {
        public sealed class Reused : PairingAnswer
        {
        }

        public sealed class Final : PairingAnswer
        {
            public Final(PairStatus status) => Status = status;

            public PairStatus Status { get; }
        }

        public sealed class Wait : PairingAnswer
        {
            public Wait(Task<PairStatus> status) => Status = status;

            public Task<PairStatus> Status { get; }
        }
    }

    /// <summary>
    /// Decides what a device holding the right code is told. Runs under the lock without waiting, so two devices
    /// arriving together cannot both be treated as the first to use the code.
    /// </summary>
    private PairingAnswer PairingAnswerFor(byte[] keyBytes, PairRequest request, string address)
    {
        var current = window;
        if (current is null)
        {
            return new PairingAnswer.Reused();
        }
        var key = Hex.Encode(keyBytes);

        switch (current.Use)
        {
            case WindowUse.Unused:
            {
                var name = TextRules.SanitizeDeviceName(request.Name);
                var approval = new ApprovalRequest(
                    name.Length == 0 ? "Unnamed device" : name,
                    IntegrationV1.Fingerprint(keyBytes),
                    ItemKinds.CanonicalList(request.Kinds),
                    address,
                    removedKeys.Contains(key));
                Record(new Event.PairingRequested(approval.DeviceFingerprint));

                var race = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                var handler = approvalHandler;
                var timeout = options.ApprovalTimeout;
                var windowId = current.Id;
                var answerCancel = new CancellationTokenSource();
                var timerCancel = new CancellationTokenSource();
                // Not awaited together: a handler may never finish.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        race.TrySetResult(handler is null
                            ? ApprovalDecision.Allow(approval.RequestedKinds)
                            : await handler(approval, answerCancel.Token).ConfigureAwait(false));
                    }
                    catch (Exception)
                    {
                        race.TrySetResult(ApprovalDecision.Deny);
                    }
                });
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(timeout, timerCancel.Token).ConfigureAwait(false);
                        race.TrySetResult(ApprovalDecision.Deny);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
                var status = Task.Run(async () =>
                {
                    var decision = await race.Task.ConfigureAwait(false);
                    answerCancel.Cancel();
                    timerCancel.Cancel();
                    lock (sync)
                    {
                        return FinishPairing(keyBytes, windowId, approval, decision);
                    }
                });
                current.Use = new WindowUse.Pending(key, status, race);
                return new PairingAnswer.Wait(status);
            }
            case WindowUse.Pending pending:
                if (pending.Key != key)
                {
                    pending.Race.TrySetResult(ApprovalDecision.Deny);
                    BurnWindow();
                    return new PairingAnswer.Reused();
                }
                // The same device trying again after losing its connection.
                return new PairingAnswer.Wait(pending.Status);
            case WindowUse.Approved approved:
                if (approved.Key != key)
                {
                    BurnWindow();
                    return new PairingAnswer.Reused();
                }
                // It may have been removed since. The code does not bring it back.
                if (!devices.TryGetValue(approved.Key, out var device))
                {
                    return new PairingAnswer.Final(PairStatus.Denied.Instance);
                }
                return new PairingAnswer.Final(new PairStatus.Approved(approved.Kinds, device.LastCounter));
            case WindowUse.Denied denied:
                if (denied.Key != key)
                {
                    BurnWindow();
                    return new PairingAnswer.Reused();
                }
                return new PairingAnswer.Final(PairStatus.Denied.Instance);
            default:
                return new PairingAnswer.Reused();
        }
    }

    private PairStatus FinishPairing(byte[] keyBytes, Guid windowId, ApprovalRequest approval, ApprovalDecision decision)
    {
        var key = Hex.Encode(keyBytes);
        // The window may have been burned or replaced while the person was deciding.
        if (window is null || window.Id != windowId || window.Use is not WindowUse.Pending)
        {
            Record(new Event.PairingDenied(approval.DeviceFingerprint));
            return PairStatus.Denied.Instance;
        }
        if (isPaused || decision is not ApprovalDecision.AllowDecision allow)
        {
            window.Use = new WindowUse.Denied(key);
            Record(new Event.PairingDenied(approval.DeviceFingerprint));
            return PairStatus.Denied.Instance;
        }
        var allowed = ItemKinds.CanonicalList(allow.Kinds).Where(approval.RequestedKinds.Contains).ToArray();
        if (allowed.Length == 0)
        {
            window.Use = new WindowUse.Denied(key);
            Record(new Event.PairingDenied(approval.DeviceFingerprint));
            return PairStatus.Denied.Instance;
        }

        var finalName = allow.Name is { } given ? TextRules.SanitizeDeviceName(given) : approval.DeviceName;
        var device = new Device(keyBytes.ToArray(), finalName, allowed, options.Limits, Now());
        if (devices.TryGetValue(key, out var previous))
        {
            // Paired again while still on the list. Its items and numbering carry over.
            previous.Connection?.Stream.Cancel();
            device.Items = previous.Items;
            device.DiskBytes = previous.DiskBytes;
            device.LastSeq = previous.LastSeq;
            device.LastDigest = previous.LastDigest;
            device.LastFailure = previous.LastFailure;
            device.LastCounter = previous.LastCounter;
        }
        else
        {
            if (retainedItems.Remove(key, out var retained))
            {
                device.Items = retained;
                device.DiskBytes = retained.Values.Sum(Cost);
            }
            device.LastCounter = removedCounters.Remove(key, out var counter) ? counter : 0;
        }
        devices[key] = device;
        removedKeys.Remove(key);
        window.Use = new WindowUse.Approved(key, allowed);
        Record(new Event.Paired(approval.DeviceFingerprint, allowed));
        return new PairStatus.Approved(allowed, device.LastCounter);
    }

    private async Task RunOperationsAsync(LiveConnection connection, Device device)
    {
        var stream = connection.Stream;
        try
        {
            while (true)
            {
                stream.ArmDeadline(options.IdleTimeout);
                var frame = await stream.ReadFrameAsync().ConfigureAwait(false);
                stream.DisarmDeadline();
                lock (sync)
                {
                    if (device.Connection != connection)
                    {
                        break;
                    }
                }
                // A message that fails authentication means the session cannot be trusted any more.
                var plaintext = connection.Transport.Open(frame);

                Step step;
                lock (sync)
                {
                    step = Process(plaintext, device);
                }
                ReceiverMessage reply;
                if (step.Job is { } job)
                {
                    var decoded = await DecodePhotoAsync(job, device.KeyText, stream.Identifier).ConfigureAwait(false);
                    lock (sync)
                    {
                        // The device may have been removed, or may have reconnected, during the decode.
                        if (device.Connection != connection || !devices.TryGetValue(device.KeyText, out var still) || still != device)
                        {
                            stream.Cancel();
                            return;
                        }
                        reply = FinishPhoto(job, decoded, device);
                    }
                }
                else
                {
                    reply = step.Answer!;
                }

                // A peer that stops reading must not be able to hold this connection open.
                await connection.SendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    stream.ArmDeadline(options.SendTimeout);
                    await stream.SendFrameAsync(connection.Transport.Seal(Wire.Encode(reply))).ConfigureAwait(false);
                    stream.DisarmDeadline();
                }
                finally
                {
                    connection.SendLock.Release();
                }

                if (reply is ReceiverMessage.Error { Value.Fatal: true })
                {
                    stream.Cancel();
                    break;
                }
            }
        }
        catch (Exception error) when (error is IOException or NoiseException or ObjectDisposedException or SocketException)
        {
            stream.Cancel();
        }
        finally
        {
            lock (sync)
            {
                if (device.Connection == connection)
                {
                    device.Connection = null;
                    device.Upload = null;
                    Record(new Event.Disconnected(device.Fingerprint));
                }
            }
        }
    }

    // Requests

    private sealed record Step(ReceiverMessage? Answer, PhotoJob? Job)
    {
        public static Step Reply(ReceiverMessage answer) => new(answer, null);
    }

    private sealed record PhotoJob(PhotoHeader Header, byte[] Data, ulong Seq, byte[] Digest);

    private sealed class RetryLaterException : Exception
    {
        public RetryLaterException(double seconds) => Seconds = seconds;

        public double Seconds { get; }
    }

    /// <summary>Counts a rejection against the device, and penalises it if there have been too many.</summary>
    private ReceiverMessage Reject(Device device, ErrorCode code, ulong? seq, ulong? retryAfterMs = null, ulong? lastSeq = null)
    {
        Record(new Event.Rejected(device.Fingerprint, seq, code.ToWire()));
        var now = Now();
        device.Rejections = device.Rejections.Where(time => now - time < 60).ToList();
        device.Rejections.Add(now);
        if (device.Rejections.Count > options.MaxRejectionsPerMinute)
        {
            var penalty = device.NextPenalty;
            device.IgnoredUntil = now + penalty;
            device.NextPenalty = Math.Min(penalty * 2, 3600);
            device.Rejections.Clear();
            return new ReceiverMessage.Error(new ReceiverError(ErrorCode.TooManyErrors, seq, retryAfterMs: (ulong)(penalty * 1000), fatal: true));
        }
        return new ReceiverMessage.Error(new ReceiverError(code, seq, lastSeq, retryAfterMs));
    }

    /// <summary>Being told to slow down is not a fault, unless the device has not waited as it was asked to.</summary>
    private ReceiverMessage RateLimited(Device device, double rawWait, ulong? seq, bool forPoints = false)
    {
        var now = Now();
        var wait = Math.Min(rawWait, 3600);
        var milliseconds = (ulong)Math.Ceiling(wait * 1000);
        var cameTooSoon = forPoints
            ? device.PointRetry.ArrivalCameTooSoon(now, wait)
            : device.MessageRetry.ArrivalCameTooSoon(now, wait);
        if (cameTooSoon)
        {
            return Reject(device, ErrorCode.RateLimited, seq, retryAfterMs: milliseconds);
        }
        Record(new Event.Rejected(device.Fingerprint, seq, ErrorCode.RateLimited.ToWire()));
        return new ReceiverMessage.Error(new ReceiverError(ErrorCode.RateLimited, seq, retryAfterMs: milliseconds));
    }

    private Step Process(byte[] plaintext, Device device)
    {
        // The rate limit comes first and does not use up the sequence number.
        if (device.MessageBucket.Take(Now()) is { } wait)
        {
            return Step.Reply(RateLimited(device, wait, null));
        }

        RequestEnvelope envelope;
        try
        {
            envelope = Wire.DecodeRequest(plaintext, options.Limits);
        }
        catch (WireException error)
        {
            return Step.Reply(Reject(device, error.Code, null));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or System.Text.DecoderFallbackException)
        {
            return Step.Reply(Reject(device, ErrorCode.InvalidMessage, null));
        }

        var digest = SHA256.HashData(plaintext);
        if (envelope.Seq == device.LastSeq)
        {
            if (device.LastDigest is null || !CryptographicOperations.FixedTimeEquals(digest, device.LastDigest))
            {
                return Step.Reply(Reject(device, ErrorCode.SeqConflict, envelope.Seq, lastSeq: device.LastSeq));
            }
            return Step.Reply(device.LastFailure is { } failure
                ? new ReceiverMessage.Error(failure)
                : new ReceiverMessage.Ack(envelope.Seq, duplicate: true));
        }
        if (envelope.Seq < device.LastSeq)
        {
            return Step.Reply(Reject(device, ErrorCode.StaleSeq, envelope.Seq, lastSeq: device.LastSeq));
        }

        if (envelope.Request is Request.PhotoEnd end)
        {
            try
            {
                var upload = TakeFinishedUpload(end.Id, device);
                return new Step(null, new PhotoJob(upload.Header, upload.Received.ToArray(), envelope.Seq, digest));
            }
            catch (WireException error)
            {
                return Step.Reply(Settle(error, envelope.Seq, digest, device));
            }
        }

        try
        {
            var summary = Apply(envelope.Request, device);
            return Step.Reply(Settle(summary, envelope.Seq, digest, device));
        }
        catch (RetryLaterException later)
        {
            return Step.Reply(RateLimited(device, later.Seconds, envelope.Seq, forPoints: true));
        }
        catch (WireException error)
        {
            return Step.Reply(Settle(error, envelope.Seq, digest, device));
        }
    }

    private ReceiverMessage Settle(string summary, ulong seq, byte[] digest, Device device)
    {
        device.LastSeq = seq;
        device.LastDigest = digest;
        device.LastFailure = null;
        Record(new Event.Applied(device.Fingerprint, seq, summary));
        return new ReceiverMessage.Ack(seq, duplicate: false);
    }

    private ReceiverMessage Settle(WireException error, ulong seq, byte[] digest, Device device)
    {
        var code = error.Code;
        if (!code.IsRetryable())
        {
            // The number is used up, and a resend gets the same answer without running again.
            device.LastSeq = seq;
            device.LastDigest = digest;
            device.LastFailure = new ReceiverError(code, seq);
        }
        return Reject(device, code, seq);
    }

    // Photos

    private Upload TakeFinishedUpload(ItemId id, Device device)
    {
        RequireKind(ItemKind.Photo, device);
        if (device.Upload is not { } upload || !upload.Header.Id.Equals(id))
        {
            throw new WireException(ErrorCode.InvalidState, "no upload is in progress for this id");
        }
        device.Upload = null;
        if (upload.Received.Length != upload.Header.Size || upload.NextIndex != upload.Header.Chunks
            || Hex.Encode(SHA256.HashData(upload.Received.ToArray())) != upload.Header.Sha256)
        {
            throw new WireException(ErrorCode.PhotoInvalid, "the data does not match the declared size and hash");
        }
        return upload;
    }

    private bool IsCurrent(string deviceKey, Guid streamId) =>
        devices.TryGetValue(deviceKey, out var device) && device.Connection?.Stream.Identifier == streamId;

    /// <summary>
    /// Decodes away from the receiver's lock, so a slow image cannot hold up other devices or a removal, and one at a
    /// time, so several images cannot add up to a lot of memory. A decode nobody is waiting for any more is skipped,
    /// so a device cannot leave a backlog behind by reconnecting.
    /// </summary>
    private async Task<(byte[]? Stored, WireException? Error)> DecodePhotoAsync(PhotoJob job, string deviceKey, Guid streamId)
    {
        await photoDecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (!IsCurrent(deviceKey, streamId))
                {
                    return (null, new WireException(ErrorCode.InternalError, "the connection ended"));
                }
            }
            return await Task.Run<(byte[]?, WireException?)>(() =>
            {
                try
                {
                    return (PhotoPipeline.Process(job.Data, job.Header.Type), null);
                }
                catch (WireException error)
                {
                    return (null, error);
                }
                catch (Exception)
                {
                    return (null, new WireException(ErrorCode.InternalError, "the image could not be processed"));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            photoDecodeGate.Release();
        }
    }

    private ReceiverMessage FinishPhoto(PhotoJob job, (byte[]? Stored, WireException? Error) decoded, Device device)
    {
        try
        {
            if (decoded.Error is { } failure)
            {
                throw failure;
            }
            // The id may have been taken by a put since the upload began.
            var id = job.Header.Id.Value;
            if (device.Items.TryGetValue(id, out var existing))
            {
                if (existing.Kind != ItemKind.Photo)
                {
                    throw new WireException(ErrorCode.KindConflict, "this id is already used by another kind of item");
                }
            }
            else if (device.Items.Values.Count(item => item.Kind == ItemKind.Photo) >= options.Limits.Photos)
            {
                throw new WireException(ErrorCode.LimitPhotos, "the photo limit was reached");
            }
            var item = BlankItem(job.Header.Id, ItemKind.Photo, job.Header.At) with { Title = job.Header.Title, Photo = decoded.Stored };
            Store(item, device);
            return Settle("photo stored", job.Seq, job.Digest, device);
        }
        catch (WireException error)
        {
            return Settle(error, job.Seq, job.Digest, device);
        }
    }

    // Items

    private static void RequireKind(ItemKind kind, Device device)
    {
        if (!device.Kinds.Contains(kind))
        {
            throw new WireException(ErrorCode.NotPermitted, "this device may not send this kind");
        }
    }

    private void Charge(long bytes, Device device)
    {
        if (device.DiskBytes + bytes > options.Limits.DiskBytes || TotalDiskBytes + bytes > options.DiskBytesTotal)
        {
            throw new WireException(ErrorCode.LimitDisk, "the storage limit was reached");
        }
        device.DiskBytes += bytes;
    }

    private static long Cost(Item item)
    {
        long total = 256 + System.Text.Encoding.UTF8.GetByteCount(item.Title ?? string.Empty) + System.Text.Encoding.UTF8.GetByteCount(item.Text ?? string.Empty);
        total += item.Photo?.Length ?? 0;
        total += item.Points.Count * 32L;
        return total;
    }

    private void Store(Item item, Device device)
    {
        var previous = device.Items.TryGetValue(item.Id, out var existing) ? Cost(existing) : 0;
        var next = Cost(item);
        if (next > previous)
        {
            Charge(next - previous, device);
        }
        else
        {
            device.DiskBytes -= previous - next;
        }
        device.Items[item.Id] = item;
    }

    private static void Discard(string id, Device device)
    {
        if (device.Items.Remove(id, out var item))
        {
            device.DiskBytes -= Cost(item);
        }
    }

    private void PutBasic(Item item, Device device)
    {
        RequireKind(item.Kind, device);
        if (device.Items.TryGetValue(item.Id, out var existing))
        {
            if (existing.Kind != item.Kind)
            {
                throw new WireException(ErrorCode.KindConflict, "this id is already used by another kind of item");
            }
        }
        else if (device.Items.Values.Count(stored => stored.Kind.UsesPut()) >= options.Limits.Items)
        {
            throw new WireException(ErrorCode.LimitItems, "the item limit was reached");
        }
        Store(item, device);
    }

    private static Item BlankItem(ItemId id, ItemKind kind, Coordinate? at) =>
        new(id.Value, kind) { Latitude = at?.Latitude, Longitude = at?.Longitude };

    /// <summary>Applies one request to this device's items, and to nothing else. Returns a description for the event log.</summary>
    private string Apply(Request request, Device device)
    {
        switch (request)
        {
            case Request.Ping:
                return "ping";
            case Request.PutPin pin:
                PutBasic(BlankItem(pin.Id, ItemKind.Pin, pin.At) with { Title = pin.Title }, device);
                return "put pin";
            case Request.PutMarker marker:
                PutBasic(BlankItem(marker.Id, ItemKind.Marker, marker.At) with { Title = marker.Title, Color = marker.Color?.ToWire() }, device);
                return "put marker";
            case Request.PutNote note:
                PutBasic(BlankItem(note.Id, ItemKind.Note, note.At) with { Text = note.Text }, device);
                return "put note";
            case Request.Move move:
            {
                if (!device.Items.TryGetValue(move.Id.Value, out var item))
                {
                    throw new WireException(ErrorCode.NotFound, "no such item");
                }
                if (item.Kind == ItemKind.Track)
                {
                    throw new WireException(ErrorCode.InvalidState, "a track cannot be moved");
                }
                RequireKind(item.Kind, device);
                device.Items[move.Id.Value] = item with { Latitude = move.To.Latitude, Longitude = move.To.Longitude };
                return "move";
            }
            case Request.Delete delete:
                Discard(delete.Id.Value, device);
                if (device.Upload?.Header.Id.Equals(delete.Id) == true)
                {
                    device.Upload = null;
                }
                return "delete";
            case Request.DeleteAll:
                device.Items.Clear();
                device.Upload = null;
                device.DiskBytes = 0;
                return "delete all";
            case Request.PhotoBegin begin:
            {
                var header = begin.Header;
                RequireKind(ItemKind.Photo, device);
                if (device.Items.TryGetValue(header.Id.Value, out var existing))
                {
                    if (existing.Kind != ItemKind.Photo)
                    {
                        throw new WireException(ErrorCode.KindConflict, "this id is already used by another kind of item");
                    }
                }
                else if (device.Items.Values.Count(item => item.Kind == ItemKind.Photo) >= options.Limits.Photos)
                {
                    throw new WireException(ErrorCode.LimitPhotos, "the photo limit was reached");
                }
                device.Upload = new Upload(header);
                return "photo begin";
            }
            case Request.PhotoChunk chunk:
            {
                RequireKind(ItemKind.Photo, device);
                if (device.Upload is not { } upload || !upload.Header.Id.Equals(chunk.Id))
                {
                    throw new WireException(ErrorCode.InvalidState, "no upload is in progress for this id");
                }
                if (chunk.Index != upload.NextIndex)
                {
                    device.Upload = null;
                    throw new WireException(ErrorCode.InvalidState, "photo chunks must arrive in order");
                }
                if (upload.Received.Length + chunk.Data.Length > upload.Header.Size)
                {
                    device.Upload = null;
                    throw new WireException(ErrorCode.PhotoInvalid, "more data arrived than was declared");
                }
                upload.Received.Write(chunk.Data);
                upload.NextIndex += 1;
                return "photo chunk";
            }
            case Request.PhotoEnd:
                // Handled before this point, because decoding happens away from the lock.
                throw new WireException(ErrorCode.InternalError, "unexpected request");
            case Request.TrackAppend append:
            {
                RequireKind(ItemKind.Track, device);
                if (device.PointBucket.Take(Now(), append.Points.Count) is { } wait)
                {
                    throw new RetryLaterException(wait);
                }
                Item item;
                if (device.Items.TryGetValue(append.Id.Value, out var existing))
                {
                    if (existing.Kind != ItemKind.Track)
                    {
                        throw new WireException(ErrorCode.KindConflict, "this id is already used by another kind of item");
                    }
                    if (existing.IsFinished)
                    {
                        throw new WireException(ErrorCode.InvalidState, "this track is already finished");
                    }
                    item = existing;
                }
                else
                {
                    var tracks = device.Items.Values.Where(stored => stored.Kind == ItemKind.Track).ToList();
                    if (tracks.Count >= options.Limits.Tracks)
                    {
                        throw new WireException(ErrorCode.LimitTracks, "too many tracks");
                    }
                    if (tracks.Count(track => !track.IsFinished) >= options.Limits.OpenTracks)
                    {
                        throw new WireException(ErrorCode.LimitTracks, "too many open tracks");
                    }
                    item = BlankItem(append.Id, ItemKind.Track, null);
                }
                var points = item.Points.Concat(append.Points).ToList();
                // Oldest points go first once a track is full, so long recordings keep working.
                if (points.Count > options.Limits.TrackPoints)
                {
                    points.RemoveRange(0, points.Count - options.Limits.TrackPoints);
                }
                Store(item with { Title = append.Title ?? item.Title, Points = points }, device);
                return "track append";
            }
            case Request.TrackFinish finish:
            {
                RequireKind(ItemKind.Track, device);
                if (!device.Items.TryGetValue(finish.Id.Value, out var item))
                {
                    throw new WireException(ErrorCode.NotFound, "no such item");
                }
                if (item.Kind != ItemKind.Track)
                {
                    throw new WireException(ErrorCode.KindConflict, "this id is not a track");
                }
                device.Items[finish.Id.Value] = item with { IsFinished = true };
                return "track finish";
            }
            default:
                throw new WireException(ErrorCode.InvalidMessage, "unknown request");
        }
    }
}
