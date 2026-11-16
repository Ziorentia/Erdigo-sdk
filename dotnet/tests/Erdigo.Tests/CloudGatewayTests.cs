extern alias gateway;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;
using Xunit;
using GatewayCommand = gateway::Erdigo.Examples.CloudGateway.Gateway;
using GatewayTimings = gateway::Erdigo.Examples.CloudGateway.GatewayTimings;
using RelayAddress = gateway::Erdigo.Examples.CloudGateway.RelayAddress;
using Receiver = Erdigo.MockReceiver.MockReceiver;

namespace Erdigo.Tests;

/// <summary>
/// The gateway of examples/CloudGateway between the relay of examples/CloudRelay and the mock receiver, all over real
/// connections on the loopback interface, as its command line runs them.
/// </summary>
public sealed class CloudGatewayTests : EndToEndBase, IDisposable
{
    private static readonly GatewayTimings Fast = new()
    {
        WaitSeconds = 1,
        RequestTimeout = TimeSpan.FromSeconds(10),
        OnceRequestTimeout = TimeSpan.FromSeconds(5),
        GiveUpAfter = TimeSpan.FromSeconds(3),
        FirstRetrySeconds = 0.1,
        LongestRetrySeconds = 0.5,
        SdkConnectTimeout = TimeSpan.FromSeconds(1),
        NoAnswerNotice = TimeSpan.FromSeconds(0.2),
        EmptyFetchInterval = TimeSpan.FromSeconds(0.05),
    };

    private readonly string root = Path.Combine(Path.GetTempPath(), "erdigo-gateway-tests", Guid.NewGuid().ToString("N"));

    private string StateDir => Path.Combine(root, "state");

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed record Result(int Code, string Output, string Error)
    {
        public string[] Lines => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
    }

    private static Task<Result> GatewayAsync(Func<string, string?> environment, CancellationToken stop, params string[] args) =>
        GatewayAsync(Fast, environment, stop, args);

    private static async Task<Result> GatewayAsync(GatewayTimings timings, Func<string, string?> environment, CancellationToken stop, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await GatewayCommand.RunAsync(args, environment, TextWriter.Synchronized(output), TextWriter.Synchronized(error), stop, timings);
        return new Result(code, output.ToString(), error.ToString());
    }

    private static Func<string, string?> Token(string? token) => name => name == "ERDIGO_RELAY_TOKEN" ? token : null;

    private async Task PairAsync(Receiver receiver, ushort port, string? kinds = null)
    {
        Directory.CreateDirectory(root);
        var codeFile = Path.Combine(root, "code");
        PrivateFile.WriteAllBytes(codeFile, Encoding.UTF8.GetBytes(receiver.OpenPairingWindow().Uri!));
        var args = new List<string> { "pair", "--name", "Cloud gateway", "--host", "127.0.0.1", "--port", Port(port), "--code-file", codeFile, "--state-dir", StateDir };
        if (kinds is not null)
        {
            args.AddRange(new[] { "--kinds", kinds });
        }
        var result = await GatewayAsync(_ => null, CancellationToken.None, args.ToArray());
        Assert.True(result.Code == 0, result.Error);
        Assert.Contains("Paired. Allowed: ", result.Output, StringComparison.Ordinal);
    }

    private static string Port(ushort port) => port.ToString(CultureInfo.InvariantCulture);

    private Task<Result> RunOnceAsync(string url, string? token, ushort port, CancellationToken stop = default) =>
        GatewayAsync(Token(token), stop, "run", "--relay", url, "--host", "127.0.0.1", "--port", Port(port), "--once", "--state-dir", StateDir);

    private Task<Result> RunOnceAsync(TestRelay relay, ushort port) => RunOnceAsync(relay.Url, relay.Gateway, port);

    private static string Device(Receiver receiver) => receiver.PairedDevices().Single().Fingerprint;

    private static Receiver.Item Item(Receiver receiver, string id) => receiver.Items(Device(receiver)).Single(item => item.Id == id);

    private static string Ops(params string[] operations) => $"{{\"ops\":[{string.Join(",", operations)}]}}";

    private static string PinOp(string id, double lat = 37.5663, double lon = 126.9779) =>
        string.Create(CultureInfo.InvariantCulture, $$"""{"op":"pin","id":"{{id}}","lat":{{lat}},"lon":{{lon}}}""");

    private static string EveryKind() => Ops(
        """{"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"}""",
        """{"op":"marker","id":"gate","lat":37.5796,"lon":126.977,"title":"Gate","color":"red"}""",
        """{"op":"marker","id":"old","lat":1,"lon":2}""",
        """{"op":"note","id":"memo","lat":0,"lon":0,"text":"line one\nline\ttwo"}""",
        $$"""{"op":"photo","id":"view","lat":37.5663,"lon":126.9779,"type":"image/png","data":"{{RelayCases.TinyPng()}}","title":"View"}""",
        """{"op":"track","id":"flight-7","title":"Flight 7","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":120},{"lat":37.5664,"lon":126.9779,"ts":1700000001000},{"lat":37.5665,"lon":126.978}]}""",
        """{"op":"finish_track","id":"flight-7"}""",
        """{"op":"move","id":"drone-1","lat":37.57,"lon":126.98}""",
        """{"op":"delete","id":"old"}""");

    [Fact]
    public async Task EveryKindOfOperationReachesTheApp()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync();
        Assert.Equal("""{"accepted":9,"last_seq":9}""", (await relay.PostAsync(EveryKind())).Text);

        var result = await RunOnceAsync(relay, port);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal(new[] { "Done with 9 operations, up to 9" }, result.Lines);
        Assert.Empty(result.Error);

        var pin = Item(receiver, "drone-1");
        Assert.Equal(ItemKind.Pin, pin.Kind);
        Assert.Equal(37.57, pin.Latitude);
        Assert.Equal(126.98, pin.Longitude);
        Assert.Equal("Drone 1", pin.Title);
        var marker = Item(receiver, "gate");
        Assert.Equal("red", marker.Color);
        Assert.Equal("Gate", marker.Title);
        Assert.Equal(37.5796, marker.Latitude);
        Assert.DoesNotContain(receiver.Items(Device(receiver)), item => item.Id == "old");
        Assert.Equal("line one\nline\ttwo", Item(receiver, "memo").Text);
        var photo = Item(receiver, "view");
        Assert.Equal(ItemKind.Photo, photo.Kind);
        Assert.Equal("View", photo.Title);
        Assert.NotNull(photo.Photo);
        var track = Item(receiver, "flight-7");
        Assert.True(track.IsFinished);
        Assert.Equal("Flight 7", track.Title);
        Assert.Equal(3, track.Points.Count);
        Assert.Equal(1_700_000_000_000L, track.Points[0].TimestampMs);
        Assert.Equal(120.0, track.Points[0].AltitudeM);
        Assert.Null(track.Points[2].TimestampMs);
        Assert.Equal(5, receiver.Items(Device(receiver)).Count);

        // The last fetch told the relay it could forget them all.
        Assert.Equal(0, relay.Server.Queue.Size.Count);

        var state = Path.Combine(StateDir, "relay-state.json");
        using (var document = JsonDocument.Parse(File.ReadAllText(state)))
        {
            Assert.Equal(relay.Url, document.RootElement.GetProperty("relay").GetString());
            Assert.Equal(9, document.RootElement.GetProperty("seq").GetInt64());
        }
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state));
        }
        Assert.Empty(Directory.GetFiles(StateDir, ".relay-state.json.*"));

        // It says what it did, never what the operations held, nor the token.
        foreach (var secret in new[] { "drone-1", "Drone 1", "line one", "37.5663", "View", relay.Gateway })
        {
            Assert.DoesNotContain(secret, result.Output + result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunningAgainAppliesNothingTwice()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(EveryKind());
        Assert.Equal(0, (await RunOnceAsync(relay, port)).Code);
        var device = Device(receiver);
        var lastSeq = receiver.LastSeq(device);

        var again = await RunOnceAsync(relay, port);
        Assert.Equal(0, again.Code);
        Assert.Empty(again.Lines);
        Assert.Equal(lastSeq, receiver.LastSeq(device));

        // Without its place, the gateway starts from the relay's oldest operation, and the relay holds none.
        File.Delete(Path.Combine(StateDir, "relay-state.json"));
        var fresh = await RunOnceAsync(relay, port);
        Assert.Equal(0, fresh.Code);
        Assert.Empty(fresh.Lines);
        Assert.Equal(lastSeq, receiver.LastSeq(device));
        Assert.Equal(3, Item(receiver, "flight-7").Points.Count);

        await relay.PostAsync(Ops(PinOp("drone-2")));
        var next = await RunOnceAsync(relay, port);
        Assert.Equal(new[] { "Done with 1 operation, up to 10" }, next.Lines);
    }

    [Fact]
    public async Task KindsThePersonDidNotAllowAreRefusedAndSkipped()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port, "pin,note");
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(
            """{"op":"marker","id":"m1","lat":1,"lon":2}""",
            PinOp("p1"),
            $$"""{"op":"photo","id":"ph1","lat":0,"lon":0,"type":"image/png","data":"{{RelayCases.TinyPng()}}"}""",
            """{"op":"note","id":"n1","lat":0,"lon":0,"text":"hello"}""",
            """{"op":"track","id":"t1","points":[{"lat":1,"lon":2}]}"""));

        var result = await RunOnceAsync(relay, port);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal(
            new[]
            {
                "Refused operation 1 (marker): not_permitted",
                "Refused operation 3 (photo): not_permitted",
                "Refused operation 5 (track): not_permitted",
                "Done with 5 operations, up to 5 (3 refused)",
            },
            result.Lines);
        Assert.Equal(new[] { "n1", "p1" }, receiver.Items(Device(receiver)).Select(item => item.Id).ToArray());
        Assert.Equal(0, relay.Server.Queue.Size.Count);
    }

    [Fact]
    public async Task WhenTheAppCannotBeReachedTheOperationsStayOnTheRelay()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        receiver.Stop();
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(PinOp("p1"), PinOp("p2")));

        var clock = Stopwatch.StartNew();
        var result = await RunOnceAsync(relay, port);
        Assert.Equal(3, result.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"gave up after {clock.Elapsed}");
        Assert.Single(result.Lines, "No answer from the Erdigo app; trying again.");
        Assert.Equal("The Erdigo app could not be reached. The operations stay on the relay.", result.Lines[^1]);
        Assert.Equal(2, relay.Server.Queue.Size.Count);
        var held = await relay.GetAsync();
        Assert.Equal(2, held.Json.GetProperty("ops").GetArrayLength());
    }

    [Fact]
    public async Task AnAnswerLostOnTheWayIsSentAgainSoTheAppAppliesItOnce()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var proxy = AnswerLosingProxy.Start(port, downtime: TimeSpan.FromSeconds(1.5));
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops("""{"op":"track","id":"walk","points":[{"lat":0,"lon":0,"ts":1700000000000}]}""", PinOp("p1")));

        // Long enough for the outage, so --once does not give up.
        var timings = Fast with { GiveUpAfter = TimeSpan.FromSeconds(10) };
        var result = await GatewayAsync(
            timings, Token(relay.Gateway), CancellationToken.None,
            "run", "--relay", relay.Url, "--host", "127.0.0.1", "--port", Port(proxy.Port), "--once", "--state-dir", StateDir);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.True(proxy.LostAnAnswer);
        // The app applied the track's point before its answer was lost. The same request, sent again with the same
        // number, is recognised and not applied a second time.
        Assert.Single(Item(receiver, "walk").Points);
        Assert.Equal(ItemKind.Pin, Item(receiver, "p1").Kind);
        Assert.Equal(
            new[] { "No answer from the Erdigo app; trying again.", "Done with 2 operations, up to 2" },
            result.Lines);
        Assert.Equal(0, relay.Server.Queue.Size.Count);
    }

    private const string WalkOp = """{"op":"track","id":"walk","points":[{"lat":1,"lon":2},{"lat":1.5,"lon":2.5}]}""";

    private static readonly string[] GaveUpOnTheApp =
    {
        "No answer from the Erdigo app; trying again.",
        "The Erdigo app could not be reached. The operations stay on the relay.",
    };

    [Fact]
    public async Task WithOnceAnAppWhoseAnswersAreAllLostIsGivenUpOn()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var proxy = AnswerLosingProxy.Start(port, Losing.Every);
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(WalkOp));

        var clock = Stopwatch.StartNew();
        var result = await GatewayAsync(
            Token(relay.Gateway), CancellationToken.None,
            "run", "--relay", relay.Url, "--host", "127.0.0.1", "--port", Port(proxy.Port), "--once", "--state-dir", StateDir);
        Assert.True(result.Code == 3, result.Output + result.Error);
        // The app took a new connection each time, which starts the SDK's own count again, so only the gateway's count
        // for the operation ends this.
        Assert.True(proxy.Lost > 1, $"{proxy.Lost} answers lost");
        Assert.True(clock.Elapsed >= Fast.GiveUpAfter && clock.Elapsed < TimeSpan.FromSeconds(15), $"gave up after {clock.Elapsed}");
        Assert.Equal(GaveUpOnTheApp, result.Lines);
        Assert.Equal(1, relay.Server.Queue.Size.Count);
        // The app applied the first request. Each resend carried the same number, so it was not applied again.
        Assert.Equal(2, Item(receiver, "walk").Points.Count);
    }

    [Fact]
    public async Task WithOnceAnAppThatTakesTheConnectionAndNeverAnswersIsGivenUpOn()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var proxy = AnswerLosingProxy.Start(port, Losing.Hold);
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(WalkOp));

        // The trouble shows only when the SDK stops waiting for the answer and drops the connection.
        var timings = Fast with { SdkAckTimeout = TimeSpan.FromSeconds(1) };
        var clock = Stopwatch.StartNew();
        var result = await GatewayAsync(
            timings, Token(relay.Gateway), CancellationToken.None,
            "run", "--relay", relay.Url, "--host", "127.0.0.1", "--port", Port(proxy.Port), "--once", "--state-dir", StateDir);
        Assert.True(result.Code == 3, result.Output + result.Error);
        Assert.True(proxy.Lost > 1, $"{proxy.Lost} answers held back");
        Assert.True(clock.Elapsed >= timings.GiveUpAfter && clock.Elapsed < TimeSpan.FromSeconds(15), $"gave up after {clock.Elapsed}");
        Assert.Equal(GaveUpOnTheApp, result.Lines);
        Assert.Equal(1, relay.Server.Queue.Size.Count);
        var held = await relay.GetAsync();
        Assert.Equal(1, held.Json.GetProperty("ops").GetArrayLength());
    }

    [Fact]
    public async Task WithOnceAnOperationTheSdkPacesIsNotGivenUpOn()
    {
        // After a burst of 100, the app takes 20 points a second, so the second track waits about 5 seconds before it is
        // sent: longer than --once waits for an operation in trouble. Waiting for the app's limits is no trouble.
        var options = FastOptions();
        options.Limits = options.Limits with { PointsPerSecond = 20, PointBurst = 100 };
        var (receiver, port) = await StartReceiverAsync(options);
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync();
        var points = string.Join(",", Enumerable.Range(0, 100).Select(index => string.Create(CultureInfo.InvariantCulture, $$"""{"lat":1,"lon":{{index}}}""")));
        await relay.PostAsync(Ops($$"""{"op":"track","id":"a","points":[{{points}}]}""", $$"""{"op":"track","id":"b","points":[{{points}}]}"""));

        var clock = Stopwatch.StartNew();
        var result = await RunOnceAsync(relay, port);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.True(clock.Elapsed > Fast.GiveUpAfter, $"done after {clock.Elapsed}");
        Assert.Equal(new[] { "Done with 2 operations, up to 2" }, result.Lines);
        Assert.Equal(100, Item(receiver, "b").Points.Count);
    }

    [Fact]
    public async Task APausedAppIsWaitedForUntilTheGatewayIsStopped()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        receiver.SetPaused(true);
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(PinOp("p1")));

        using var stop = new CancellationTokenSource();
        var running = GatewayAsync(Token(relay.Gateway), stop.Token, "run", "--relay", relay.Url, "--host", "127.0.0.1", "--port", Port(port), "--state-dir", StateDir);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(running.IsCompleted);
        receiver.SetPaused(false);
        for (var attempt = 0; attempt < 100 && receiver.Items(Device(receiver)).Count == 0; attempt++)
        {
            await Task.Delay(100);
        }
        await relay.PostAsync(Ops(PinOp("p2")));
        for (var attempt = 0; attempt < 100 && receiver.Items(Device(receiver)).Count < 2; attempt++)
        {
            await Task.Delay(100);
        }
        stop.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal("Waiting for operations from the relay. Press Ctrl+C to stop.", result.Lines[0]);
        Assert.Single(result.Lines, "No answer from the Erdigo app; trying again.");
        Assert.Contains("Done with 1 operation, up to 1", result.Lines);
        Assert.Contains("Done with 1 operation, up to 2", result.Lines);
        Assert.Equal("Stopped.", result.Lines[^1]);
        Assert.Equal(new[] { "p1", "p2" }, receiver.Items(Device(receiver)).Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task ARelayThatStartsAgainIsFollowed()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        var relay = await TestRelay.StartAsync();
        var relayPort = relay.Server.Port;
        var token = relay.Gateway;
        var sources = relay.Sources;
        await relay.PostAsync(Ops(PinOp("p1"), PinOp("p2")));
        Assert.Equal(new[] { "Done with 2 operations, up to 2" }, (await RunOnceAsync(relay, port)).Lines);
        await relay.DisposeAsync();

        // The same address, with its numbering started again.
        await using var restarted = await TestRelay.StartAsync(port: relayPort, sources: sources, gateway: token);
        Assert.Equal(relay.Url, restarted.Url);
        await restarted.PostAsync(Ops(PinOp("p3")));
        var result = await RunOnceAsync(restarted, port);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal(
            new[] { "The relay started a new numbering; starting from its oldest operation.", "Done with 1 operation, up to 1" },
            result.Lines);
        Assert.Equal(new[] { "p1", "p2", "p3" }, receiver.Items(Device(receiver)).Select(item => item.Id).ToArray());

        // Another relay's address is another place: no message, and it starts from that relay's oldest operation.
        await using var other = await TestRelay.StartAsync(sources: sources, gateway: token);
        await other.PostAsync(Ops(PinOp("p4")));
        Assert.Equal(new[] { "Done with 1 operation, up to 1" }, (await RunOnceAsync(other, port)).Lines);
    }

    [Fact]
    public async Task OperationsThatExpiredOnTheRelayAreReported()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync(keepHours: 1);
        await relay.PostAsync(Ops(PinOp("p1")));
        relay.NowMs += 2 * 3_600_000L;
        await relay.PostAsync(Ops(PinOp("p2")));
        var result = await RunOnceAsync(relay, port);
        Assert.Equal(
            new[] { "Some operations expired on the relay before they were fetched.", "Done with 1 operation, up to 2" },
            result.Lines);
        Assert.Equal(new[] { "p2" }, receiver.Items(Device(receiver)).Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task AnExpiryIsReportedOnceWhileTheGatewayWaits()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync(keepHours: 1);
        await relay.PostAsync(Ops(PinOp("p1")));
        relay.NowMs += 2 * 3_600_000L;

        using var stop = new CancellationTokenSource();
        var running = GatewayAsync(Token(relay.Gateway), stop.Token, "run", "--relay", relay.Url, "--host", "127.0.0.1", "--port", Port(port), "--state-dir", StateDir);
        // Nothing is left above the gateway's place, so the relay answers 'missed' to each of these fetches.
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.False(running.IsCompleted);
        await relay.PostAsync(Ops(PinOp("p2")));
        for (var attempt = 0; attempt < 100 && receiver.Items(Device(receiver)).Count == 0; attempt++)
        {
            await Task.Delay(100);
        }
        await Task.Delay(TimeSpan.FromSeconds(0.5));
        stop.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal(
            new[]
            {
                "Waiting for operations from the relay. Press Ctrl+C to stop.",
                "Some operations expired on the relay before they were fetched.",
                "Done with 1 operation, up to 2",
                "Stopped.",
            },
            result.Lines);
        Assert.Equal(new[] { "p2" }, receiver.Items(Device(receiver)).Select(item => item.Id).ToArray());
    }

    [Theory]
    [InlineData("http://localhost:9", "http://localhost:9")]
    [InlineData("http://LOCALHOST", "http://localhost")]
    [InlineData("http://Localhost:9/", "http://localhost:9")]
    [InlineData("http://127.0.0.1:9/relay/", "http://127.0.0.1:9/relay")]
    [InlineData("http://127.1:9", "http://127.0.0.1:9")]
    [InlineData("http://127.200.0.1:9", "http://127.200.0.1:9")]
    [InlineData("http://[::1]:9", "http://[::1]:9")]
    [InlineData("HTTPS://Relay.Example.com", "https://relay.example.com")]
    public void PlainHttpIsAcceptedForEveryLoopbackSpelling(string given, string key) =>
        Assert.Equal(key, RelayAddress.Parse(given).Key);

    [Theory]
    [InlineData("http://localhost.example.com")]
    [InlineData("http://127.0.0.1.example.com")]
    [InlineData("http://0.0.0.0:9")]
    [InlineData("http://0:9")]
    [InlineData("http://[::]:9")]
    [InlineData("http://192.0.2.1:9")]
    [InlineData("http://evil.example#@127.0.0.1")]
    [InlineData("http://127.0.0.1@evil.example")]
    [InlineData("http://[::ffff:192.0.2.1]:9")]
    public void PlainHttpIsRefusedForEveryOtherHost(string given) =>
        Assert.Throws<gateway::Erdigo.Send.UsageException>(() => RelayAddress.Parse(given));

    [Fact]
    public async Task ALoopbackRelayNamedInCapitalsIsUsed()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(PinOp("p1")));
        var url = relay.Url.Replace("127.0.0.1", "LOCALHOST", StringComparison.Ordinal);
        var result = await RunOnceAsync(url, relay.Gateway, port);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal(new[] { "Done with 1 operation, up to 1" }, result.Lines);
    }

    [Fact]
    public async Task MistakesInTheCommandLineOrTheEnvironmentExitWithOne()
    {
        var (receiver, port) = await StartReceiverAsync();
        await using var relay = await TestRelay.StartAsync();
        var token = relay.Gateway;

        // Before pairing: nothing to run with.
        var unpaired = await RunOnceAsync(relay, port);
        Assert.Equal(1, unpaired.Code);
        Assert.Contains("no pairing", unpaired.Error, StringComparison.Ordinal);

        await PairAsync(receiver, port);
        var cases = new (string[] Args, string? Token)[]
        {
            (new[] { "run", "--relay", "http://relay.example.com" }, token),
            (new[] { "run", "--relay", "http://192.0.2.1:8080" }, token),
            (new[] { "run", "--relay", "ftp://relay.example.com" }, token),
            (new[] { "run", "--relay", "relay.example.com" }, token),
            (new[] { "run", "--relay", "https://user:secret@relay.example.com" }, token),
            (new[] { "run", "--relay", "https://relay.example.com/?x=1" }, token),
            (new[] { "run", "--relay", relay.Url }, null),
            (new[] { "run", "--relay", relay.Url }, "   "),
            (new[] { "run", "--relay", relay.Url }, token[..31]),
            (new[] { "run", "--relay", relay.Url }, token + " x"),
            (new[] { "run" }, token),
            (new[] { "run", "--relay", relay.Url, "--token", token }, token),
            (new[] { "run", "--relay", relay.Url, "--name", "x" }, token),
            (new[] { "run", "--relay", relay.Url, "--host", "127.0.0.1" }, token),
            (new[] { "run", "--relay", relay.Url, "--profile", "other" }, token),
            (new[] { "fly" }, token),
        };
        foreach (var (args, given) in cases)
        {
            // Every case keeps to the test's own folder, so nothing is made in the default profile's folder.
            var full = args.Length > 1 && args[0] == "run" ? args.Concat(new[] { "--state-dir", StateDir }).ToArray() : args;
            var result = await GatewayAsync(Token(given), CancellationToken.None, full);
            Assert.True(result.Code == 1, $"{string.Join(" ", args.Select(arg => arg == token ? "<token>" : arg))}: exit {result.Code} {result.Error}");
            Assert.StartsWith("erdigo-gateway: ", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(token, result.Output + result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", result.Error, StringComparison.Ordinal);
        }
        // A profile holds one pairing.
        var second = await GatewayAsync(_ => null, CancellationToken.None, "pair", "--name", "Again", "--host", "127.0.0.1", "--port", Port(port), "--code-file", Path.Combine(root, "code"), "--state-dir", StateDir);
        Assert.Equal(1, second.Code);
        Assert.Contains("already paired", second.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyOneRunAtATimeMayUseAProfile()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(PinOp("p1")));

        using var stop = new CancellationTokenSource();
        var first = GatewayAsync(Token(relay.Gateway), stop.Token, "run", "--relay", relay.Url, "--host", "127.0.0.1", "--port", Port(port), "--state-dir", StateDir);
        // It locks the profile before its first fetch, so it holds the lock once it has written to the app.
        for (var attempt = 0; attempt < 100 && receiver.Items(Device(receiver)).Count == 0; attempt++)
        {
            await Task.Delay(100);
        }
        Assert.False(first.IsCompleted);

        var clock = Stopwatch.StartNew();
        var second = await RunOnceAsync(relay, port);
        Assert.Equal(1, second.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"refused after {clock.Elapsed}");
        Assert.Empty(second.Output);
        Assert.Equal(
            "erdigo-gateway: another 'run' is using this profile. Only one gateway at a time may use a profile or state folder.",
            Assert.Single(second.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('\r'));

        // The first goes on, and stops cleanly. Then the profile is free again.
        await relay.PostAsync(Ops(PinOp("p2")));
        for (var attempt = 0; attempt < 100 && receiver.Items(Device(receiver)).Count < 2; attempt++)
        {
            await Task.Delay(100);
        }
        stop.Cancel();
        var stopped = await first.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(stopped.Code == 0, stopped.Output + stopped.Error);
        Assert.Contains("Done with 1 operation, up to 2", stopped.Lines);
        Assert.Equal("Stopped.", stopped.Lines[^1]);
        var again = await RunOnceAsync(relay, port);
        Assert.True(again.Code == 0, again.Output + again.Error);
        Assert.Empty(again.Lines);
    }

    [Fact]
    public async Task AWrongTokenExitsWithTwo()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var relay = await TestRelay.StartAsync();
        await relay.PostAsync(Ops(PinOp("p1")));
        var wrong = TestRelay.NewToken();
        var result = await RunOnceAsync(relay.Url, wrong, port);
        Assert.Equal(2, result.Code);
        Assert.Equal("erdigo-gateway: The relay refused the token.", result.Error.TrimEnd());
        Assert.DoesNotContain(wrong, result.Output + result.Error, StringComparison.Ordinal);
        Assert.Empty(receiver.Items(Device(receiver)));
        Assert.Equal(1, relay.Server.Queue.Size.Count);
    }

    [Fact]
    public async Task ARelayThatCannotBeReachedIsTriedAgain()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var result = await RunOnceAsync($"http://127.0.0.1:{closedPort}", TestRelay.NewToken(), port);
        Assert.Equal(3, result.Code);
        Assert.Contains(result.Lines, line => line.StartsWith("The relay could not be reached; trying again in ", StringComparison.Ordinal));
        Assert.Equal("The relay could not be reached.", result.Lines[^1]);

        // A server error is tried again too.
        await using var fake = FakeRelay.Start(_ => (503, "application/json", """{"error":"queue_full"}"""));
        var busy = await RunOnceAsync(fake.Url, TestRelay.NewToken(), port);
        Assert.Equal(3, busy.Code);
        Assert.True(fake.Requests > 1);
    }

    [Fact]
    public async Task WithOnceARelayThatTakesTheConnectionAndNeverAnswersIsGivenUpOn()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var held = new List<TcpClient>();
        using var stopAccepting = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    held.Add(await silent.AcceptTcpClientAsync(stopAccepting.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        });
        try
        {
            var clock = Stopwatch.StartNew();
            var result = await RunOnceAsync($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}", TestRelay.NewToken(), port);
            Assert.Equal(3, result.Code);
            Assert.Equal("The relay could not be reached.", result.Lines[^1]);
            // The 3 seconds allowed count the time each try takes, and with --once a try ends after 5 seconds. Without
            // that, each try would take the 10-second request timeout and the count would start only after the first.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(9), string.Create(CultureInfo.InvariantCulture, $"took {clock.Elapsed.TotalSeconds:F1} s"));
        }
        finally
        {
            stopAccepting.Cancel();
            silent.Stop();
            await accepting;
            foreach (var connection in held)
            {
                connection.Dispose();
            }
        }
    }

    public static IEnumerable<object[]> BrokenAnswers() => new[]
    {
        new object[] { "not JSON", 200, "application/json", "hello" },
        new object[] { "a JSON array", 200, "application/json", "[]" },
        new object[] { "an extra field", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"missed":false,"ops":[],"more":1}""" },
        new object[] { "a missing field", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"ops":[]}""" },
        new object[] { "a short epoch", 200, "application/json", """{"epoch":"AAAA","reset":true,"missed":false,"ops":[]}""" },
        new object[] { "an epoch it was not asked about", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":false,"missed":false,"ops":[]}""" },
        new object[] { "numbers going down", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"missed":false,"ops":[{"seq":2,"received_ms":0,"op":{"op":"delete","id":"a"}},{"seq":1,"received_ms":0,"op":{"op":"delete","id":"a"}}]}""" },
        new object[] { "a number of 0", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"missed":false,"ops":[{"seq":0,"received_ms":0,"op":{"op":"delete","id":"a"}}]}""" },
        new object[] { "an operation that is not an object", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"missed":false,"ops":[{"seq":1,"received_ms":0,"op":"delete"}]}""" },
        new object[] { "a fractional time", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"missed":false,"ops":[{"seq":1,"received_ms":0.5,"op":{"op":"delete","id":"a"}}]}""" },
        new object[] { "a duplicate key", 200, "application/json", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"reset":true,"missed":false,"ops":[]}""" },
        new object[] { "text instead of JSON", 200, "text/plain", """{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"missed":false,"ops":[]}""" },
        new object[] { "a redirect", 302, "application/json", "{}" },
        new object[] { "not found", 404, "application/json", """{"error":"not_found"}""" },
        new object[] { "a bad query", 400, "application/json", """{"error":"invalid_query"}""" },
    };

    [Theory]
    [MemberData(nameof(BrokenAnswers))]
    public async Task ARelayThatBreaksTheApiExitsWithTwo(string what, int status, string type, string body)
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        await using var fake = FakeRelay.Start(_ => (status, type, body));
        var result = await RunOnceAsync(fake.Url, TestRelay.NewToken(), port);
        Assert.True(result.Code == 2, $"{what}: exit {result.Code} {result.Output} {result.Error}");
        Assert.StartsWith("erdigo-gateway: The relay's answer does not follow the relay API", result.Error, StringComparison.Ordinal);
        Assert.Empty(receiver.Items(Device(receiver)));
    }

    [Fact]
    public async Task AnAnswerOverEightMebibytesIsRefused()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        var huge = $$"""{"epoch":"AAAAAAAAAAAAAAAA","reset":true,"missed":false,"ops":[],"pad":"{{new string('x', (8 * 1024 * 1024) + 1)}}"}""";
        await using var fake = FakeRelay.Start(_ => (200, "application/json", huge));
        var result = await RunOnceAsync(fake.Url, TestRelay.NewToken(), port);
        Assert.Equal(2, result.Code);
        Assert.Contains("larger than 8 MiB", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationsTheGatewayCannotApplyAreRefusedAndSkipped()
    {
        var (receiver, port) = await StartReceiverAsync();
        await PairAsync(receiver, port);
        const string epoch = "AAAAAAAAAAAAAAAAAAAAAA";
        var first = $$$"""
            {"epoch":"{{{epoch}}}","reset":true,"missed":false,"ops":[
            {"seq":3,"received_ms":1,"op":{"op":"circle","id":"a"}},
            {"seq":4,"received_ms":1,"op":{"op":"pin","id":"a","lat":0,"lon":0,"title":"a\u0007b"}},
            {"seq":5,"received_ms":1,"op":{"op":"pin","id":"good","lat":1,"lon":2}},
            {"seq":6,"received_ms":1,"op":{"op":"photo","id":"ph","lat":0,"lon":0,"type":"image/jpeg","data":"{{{RelayCases.TinyPng()}}}"}},
            {"seq":7,"received_ms":1,"op":{"op":"move","id":"missing","lat":0,"lon":0}},
            {"seq":9,"received_ms":1,"op":{"op":"pin","id":"far","lat":95,"lon":0}},
            {"seq":10,"received_ms":1,"op":{"op":"delete","id":"has space"}},
            {"seq":11,"received_ms":1,"op":{"op":"pin","id":"extra","lat":0,"lon":0,"speed":3}}]}
            """;
        var queries = new List<string>();
        await using var fake = FakeRelay.Start(target =>
        {
            lock (queries)
            {
                queries.Add(target);
            }
            return target.Contains("after=11", StringComparison.Ordinal)
                ? (200, "application/json", $$"""{"epoch":"{{epoch}}","reset":false,"missed":false,"ops":[]}""")
                : (200, "application/json", first);
        });
        var result = await RunOnceAsync(fake.Url, TestRelay.NewToken(), port);
        Assert.True(result.Code == 0, result.Output + result.Error);
        Assert.Equal(
            new[]
            {
                "Refused operation 3 (unknown): invalid_op",
                "Refused operation 4 (pin): invalid_text",
                "Refused operation 6 (photo): invalid_op",
                "Refused operation 7 (move): not_found",
                "Refused operation 9 (pin): invalid_coordinate",
                "Refused operation 10 (delete): invalid_id",
                "Refused operation 11 (pin): invalid_op",
                "Done with 8 operations, up to 11 (7 refused)",
            },
            result.Lines);
        Assert.Equal(new[] { "good" }, receiver.Items(Device(receiver)).Select(item => item.Id).ToArray());
        Assert.Equal(2, queries.Count);
        Assert.Equal("/v1/ops?limit=100&wait=0", queries[0]);
        Assert.Equal($"/v1/ops?epoch={epoch}&after=11&limit=100&wait=0", queries[1]);
    }

    /// <summary>A stand-in for a relay that answers whatever a test tells it to, one request per connection.</summary>
    private sealed class FakeRelay : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<string, (int Status, string Type, string Body)> answer;
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        private int requests;

        private FakeRelay(Func<string, (int, string, string)> answer)
        {
            this.answer = answer;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            serving = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public int Requests => Volatile.Read(ref requests);

        public static FakeRelay Start(Func<string, (int, string, string)> answer) => new(answer);

        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var received = new StringBuilder();
                        var buffer = new byte[4096];
                        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var read = await stream.ReadAsync(buffer, stop.Token);
                            if (read == 0)
                            {
                                break;
                            }
                            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }
                        Interlocked.Increment(ref requests);
                        var target = received.ToString().Split(' ').ElementAtOrDefault(1) ?? string.Empty;
                        var (status, type, body) = answer(target);
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var head = $"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nLocation: http://127.0.0.1:1/\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token);
                        await stream.WriteAsync(bytes, stop.Token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
                    {
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await serving;
        }
    }

    /// <summary>What <see cref="AnswerLosingProxy"/> does with the receiver's answer to the first request on a connection.</summary>
    private enum Losing
    {
        /// <summary>Loses it on the first connection only and closes it, then turns connections away for a while.</summary>
        Once,

        /// <summary>Loses it on every connection, and closes the connection.</summary>
        Every,

        /// <summary>Holds it back on every connection, and keeps the connection open without another word.</summary>
        Hold,
    }

    /// <summary>
    /// Stands between the gateway and the receiver. It loses the receiver's answer to the first request on a
    /// connection, as when the app's computer drops off the network at the wrong moment, or holds it back, as an app
    /// that takes the connection and never answers.
    /// </summary>
    private sealed class AnswerLosingProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly ushort target;
        private readonly Losing losing;
        private readonly TimeSpan downtime;
        private readonly CancellationTokenSource stop = new();
        private Task accepting = Task.CompletedTask;
        private long refuseUntil;
        private int lost;

        private AnswerLosingProxy(ushort target, Losing losing, TimeSpan downtime)
        {
            this.target = target;
            this.losing = losing;
            this.downtime = downtime;
        }

        public ushort Port => (ushort)((IPEndPoint)listener.LocalEndpoint).Port;

        /// <summary>How many answers were lost or held back.</summary>
        public int Lost => Volatile.Read(ref lost);

        public bool LostAnAnswer => Lost > 0;

        public static AnswerLosingProxy Start(ushort target, TimeSpan downtime) => Start(target, Losing.Once, downtime);

        public static AnswerLosingProxy Start(ushort target, Losing losing, TimeSpan downtime = default)
        {
            var proxy = new AnswerLosingProxy(target, losing, downtime);
            proxy.listener.Start();
            proxy.accepting = Task.Run(proxy.AcceptAsync);
            return proxy;
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                if (Stopwatch.GetTimestamp() < Interlocked.Read(ref refuseUntil))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(() => ForwardAsync(client));
            }
        }

        private async Task ForwardAsync(TcpClient client)
        {
            using var server = new TcpClient();
            using (client)
            {
                try
                {
                    await server.ConnectAsync(IPAddress.Loopback, target, stop.Token);
                    var toServer = Pump(client.GetStream(), server.GetStream());
                    var toClient = FramesToClientAsync(server.GetStream(), client.GetStream());
                    await Task.WhenAny(toServer, toClient);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
                {
                }
            }
        }

        private async Task Pump(NetworkStream from, NetworkStream to)
        {
            try
            {
                await from.CopyToAsync(to, stop.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        /// <summary>Whether to lose this answer, counting it if so.</summary>
        private bool TakeAnswer() =>
            losing == Losing.Once ? Interlocked.CompareExchange(ref lost, 1, 0) == 0 : Interlocked.Increment(ref lost) > 0;

        private async Task FramesToClientAsync(NetworkStream from, NetworkStream to)
        {
            var header = new byte[2];
            try
            {
                for (var frame = 1; ; frame++)
                {
                    await from.ReadExactlyAsync(header, stop.Token);
                    var body = new byte[(header[0] << 8) | header[1]];
                    await from.ReadExactlyAsync(body, stop.Token);
                    // The receiver's third frame, after the handshake and the welcome, answers the first request.
                    if (frame == 3 && TakeAnswer())
                    {
                        if (losing == Losing.Hold)
                        {
                            // Whatever else the receiver says is dropped too, until either side closes.
                            var rest = new byte[4096];
                            while (await from.ReadAsync(rest, stop.Token) > 0)
                            {
                            }
                            return;
                        }
                        Interlocked.Exchange(ref refuseUntil, Stopwatch.GetTimestamp() + (long)(downtime.TotalSeconds * Stopwatch.Frequency));
                        return;
                    }
                    await to.WriteAsync(header, stop.Token);
                    await to.WriteAsync(body, stop.Token);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or EndOfStreamException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await accepting;
        }
    }
}
