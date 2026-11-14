using System;
using System.Text;

namespace Erdigo.Send;

/// <summary>Reads from the person at the keyboard, and only from a terminal.</summary>
internal static class TerminalInput
{
    /// <summary>True if both standard input and standard output are a terminal, so that a person can see a question and answer it.</summary>
    public static bool PersonIsThere => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>
    /// Discards anything typed so far. Only an answer given after a question has appeared counts as an answer to it.
    /// </summary>
    public static void DiscardTypedAhead()
    {
        while (Console.KeyAvailable)
        {
            Console.ReadKey(intercept: true);
        }
    }

    /// <summary>
    /// Reads one line from the keyboard, showing what is typed only if <paramref name="echo"/> is set. Returns null at
    /// the end of input (Ctrl-D, or Ctrl-Z on Windows). The characters are cleared from memory before this returns,
    /// apart from the string it returns.
    /// </summary>
    public static string? ReadLine(bool echo, int maxLength)
    {
        var buffer = new char[maxLength];
        var length = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
                {
                    if (echo)
                    {
                        Console.Out.WriteLine();
                    }
                    return new string(buffer, 0, length);
                }
                if (key.KeyChar is '\x04' or '\x1a')
                {
                    return null;
                }
                if (key.Key == ConsoleKey.Backspace || key.KeyChar is '\b' or '\x7f')
                {
                    if (length > 0)
                    {
                        length -= 1;
                        buffer[length] = '\0';
                        if (echo)
                        {
                            Console.Out.Write("\b \b");
                        }
                    }
                    continue;
                }
                if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
                {
                    continue;
                }
                if (length == buffer.Length)
                {
                    throw new UsageException("the text typed is too long");
                }
                buffer[length] = key.KeyChar;
                length += 1;
                if (echo)
                {
                    Console.Out.Write(key.KeyChar);
                }
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }
}

/// <summary>A mistake in how the tool was called. Its message never repeats what was typed.</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }
}

internal static class Text
{
    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Trims spaces and tabs, but not line breaks, from both ends.</summary>
    public static string TrimSpaces(string text)
    {
        static bool IsSpace(char character) =>
            character == '\t' || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.SpaceSeparator;
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start += 1;
        }
        while (end > start && IsSpace(text[end - 1]))
        {
            end -= 1;
        }
        return text[start..end];
    }
}
