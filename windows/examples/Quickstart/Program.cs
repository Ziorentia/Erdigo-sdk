// A complete small program: pair with a receiver once, then write a few items.
//
// Start the mock receiver in one terminal:
//
//     dotnet run --project src/erdigo-send -- serve --mock
//
// and run this in another, using the port and code it prints:
//
//     dotnet run --project examples/Quickstart -- 127.0.0.1 <port>
//
// The code is read from the keyboard. It is never passed as a command-line argument, because arguments end up in
// the process list and in shell history. If the code was typed rather than copied from the QR text, you then compare
// the receiver's key and type yes. The receiver is not asked to pair, no pairing is saved and no items are sent before
// that. That needs standard input and output to be a terminal. Without one, a typed code is refused, so use the QR
// text.

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;

if (args.Length != 2 || !ushort.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
{
    Console.WriteLine("usage: erdigo-example-quickstart <host> <port>");
    return 0;
}

try
{
    await RunAsync(ReceiverTarget.Address(args[0], port));
    return 0;
}
catch (Exception error) when (error is ErdigoException or WireException)
{
    Console.WriteLine($"Failed: {error.Message}");
    return 1;
}

static async Task RunAsync(ReceiverTarget target)
{
    // A real program would use FileDeviceStore, so the pairing survives a restart.
    var store = new InMemoryDeviceStore();
    using var identity = store.LoadOrCreateIdentity();

    var ticket = PairingTicket.Parse(ReadSecretLine("Pairing code: "));

    // Only a person can compare the key. Whatever a pipe or a file holds was written before the key existed, so it
    // is no answer, and a question sent to a file would be answered unseen. The receiver is not contacted when no
    // answer is possible.
    if (ticket.Fingerprint is null && (Console.IsInputRedirected || Console.IsOutputRedirected))
    {
        Console.Error.WriteLine("A typed code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text.");
        Environment.Exit(1);
    }

    Console.WriteLine("Contacting the receiver. If it asks, confirm the request there.");
    var pairing = await Pairing.PairAsync(
        target,
        ticket,
        identity,
        "Quickstart example",
        new[] { ItemKind.Pin, ItemKind.Marker, ItemKind.Note, ItemKind.Track },
        // A typed code proves only that the other side knows the code, so a person has to compare the receiver's
        // key. This runs before the receiver is asked to pair. A QR code carries the key, and PairAsync compares it
        // without asking.
        confirmKey: (key, _) =>
        {
            // Only an answer given after seeing the key counts, so anything typed before it is discarded.
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }
            Console.WriteLine($"Receiver key: {key}");
            Console.Write("If this matches the key shown on the receiver, type yes: ");
            // End of input and every other answer count as no.
            var answer = ReadLine(echo: true);
            var confirmed = answer?.Trim().ToLowerInvariant() == "yes";
            if (!confirmed)
            {
                Console.WriteLine();
            }
            return Task.FromResult(confirmed);
        });
    store.Save(pairing);

    using var client = new ErdigoClient(identity, pairing, store, target);

    var cityHall = new Coordinate(37.5663, 126.9779);
    await client.PutPinAsync("city-hall", cityHall, "Seoul City Hall");

    var palace = new Coordinate(37.5796, 126.9770);
    await client.PutMarkerAsync("palace", palace, "Gyeongbokgung Palace", MarkerColor.Red);
    await client.PutNoteAsync("meeting-point", palace, "Meet at the main gate.");

    // Walk from the palace to City Hall in a straight line.
    var points = Enumerable.Range(0, 5).Select(step =>
    {
        var fraction = step / 4.0;
        return new TrackPoint(new Coordinate(
            palace.Latitude + (cityHall.Latitude - palace.Latitude) * fraction,
            palace.Longitude + (cityHall.Longitude - palace.Longitude) * fraction));
    }).ToArray();
    await client.AppendTrackAsync("walk", points, "Walk to City Hall");
    await client.FinishTrackAsync("walk");

    client.Close();
    Console.WriteLine("Done.");
}

// Reads the code without showing it. With no terminal, it reads a line from standard input, which is how QR text
// reaches a program that runs without one.
static string ReadSecretLine(string prompt)
{
    if (Console.IsInputRedirected)
    {
        return Console.In.ReadLine() ?? string.Empty;
    }
    Console.Error.Write(prompt);
    var line = ReadLine(echo: false) ?? string.Empty;
    Console.Error.WriteLine();
    return line;
}

// Reads one line from the keyboard. Returns null at the end of input (Ctrl-D, or Ctrl-Z on Windows).
static string? ReadLine(bool echo)
{
    var text = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
        {
            if (echo)
            {
                Console.WriteLine();
            }
            return text.ToString();
        }
        if (key.KeyChar is '\x04' or '\x1a')
        {
            return null;
        }
        if (key.Key == ConsoleKey.Backspace || key.KeyChar is '\b' or '\x7f')
        {
            if (text.Length > 0)
            {
                text.Length -= 1;
                if (echo)
                {
                    Console.Write("\b \b");
                }
            }
            continue;
        }
        if (!char.IsControl(key.KeyChar) && text.Length < 1024)
        {
            text.Append(key.KeyChar);
            if (echo)
            {
                Console.Write(key.KeyChar);
            }
        }
    }
}
