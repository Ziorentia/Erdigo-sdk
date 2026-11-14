using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using MockConsole = Erdigo.Send.MockConsole;
using Program = Erdigo.Send.Program;
using UsageException = Erdigo.Send.UsageException;

namespace Erdigo.Tests;

/// <summary>How <c>erdigo-send serve --mock</c> decides about pairing requests.</summary>
public sealed class MockServeTests
{
    [Theory]
    // On this computer alone, every request is allowed unless --ask is given.
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, false)]
    // On the network, a person is asked unless --auto-approve is given.
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    public void WhoDecides(bool localNetwork, bool ask, bool autoApprove, bool asks)
    {
        Assert.Equal(asks, Program.AsksAboutPairing(localNetwork, ask, autoApprove));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AskingAndAllowingEverythingCannotBothBeChosen(bool localNetwork)
    {
        var error = Assert.Throws<UsageException>(() => Program.AsksAboutPairing(localNetwork, ask: true, autoApprove: true));
        Assert.Contains("cannot be used together", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Standard input that a test types into, line by line, and can close.</summary>
    private sealed class TypedInput : TextReader
    {
        private readonly BlockingCollection<string?> lines = new();

        public void Type(string line) => lines.Add(line);

        public void End() => lines.Add(null);

        public override string? ReadLine() => lines.Take();
    }

    [Fact]
    public async Task AnswersAreTakenInOrderAndOtherLinesAreCommands()
    {
        var input = new TypedInput();
        var console = new MockConsole(input);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = console.NextDecisionAsync(cancel.Token);
        var second = console.NextDecisionAsync(cancel.Token);
        input.Type(" Y ");
        input.Type("no");
        Assert.True(await first);
        Assert.False(await second);

        input.Type("devices");
        input.End();
        var commands = new System.Collections.Generic.List<string>();
        await foreach (var line in console.ReadAllAsync().WithCancellation(cancel.Token))
        {
            commands.Add(line);
        }
        Assert.Equal(new[] { "devices" }, commands);
    }

    [Fact]
    public async Task OnceInputEndsEveryQuestionIsAnsweredNoAtOnce()
    {
        var input = new TypedInput();
        var console = new MockConsole(input);
        using var never = new CancellationTokenSource();
        var waiting = console.NextDecisionAsync(never.Token);
        input.End();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        // And so is any question after that, without waiting for the approval time to run out.
        Assert.False(await console.NextDecisionAsync(never.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AQuestionThatTimesOutIsNo()
    {
        var input = new TypedInput();
        var console = new MockConsole(input);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.False(await console.NextDecisionAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        // The next line is a command, not an answer to the question that went away.
        input.Type("help");
        input.End();
        await foreach (var line in console.ReadAllAsync())
        {
            Assert.Equal("help", line);
        }
    }
}
