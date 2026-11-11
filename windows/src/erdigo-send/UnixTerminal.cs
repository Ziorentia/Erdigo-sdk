using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Erdigo.Send;

/// <summary>
/// Reads a secret from the controlling terminal with echo turned off, as <c>readpassphrase</c> does on macOS and
/// Linux. Only the echo flag of the terminal is changed, and it is put back before this returns, on Ctrl-C too.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class UnixTerminal
{
    private const int Tcsaflush = 2;
    private const uint Echo = 0x8;

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

    /// <summary>
    /// Where the local-mode flags start in <c>struct termios</c>: after three 4-byte flag words on Linux, and after
    /// three 8-byte ones on macOS. ECHO is in their lowest byte, which comes first on every little-endian system.
    /// </summary>
    private static int LocalFlagsOffset() => OperatingSystem.IsLinux() ? 12 : 24;

    /// <summary>
    /// Shows <paramref name="prompt"/> on the terminal and reads one line from it, without showing what is typed.
    /// Returns null if there is no terminal, and an empty string at the end of input.
    /// </summary>
    public static string? ReadSecretLine(string prompt, int maxLength)
    {
        FileStream terminal;
        try
        {
            terminal = new FileStream("/dev/tty", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        using (terminal)
        {
            var fd = (int)terminal.SafeFileHandle.DangerousGetHandle();
            var saved = new byte[256];
            if (tcgetattr(fd, saved) != 0)
            {
                return null;
            }
            var quiet = (byte[])saved.Clone();
            quiet[LocalFlagsOffset()] &= unchecked((byte)~Echo);
            void Restore() => tcsetattr(fd, Tcsaflush, saved);
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => Restore());
            using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Restore());
            // Anything typed before the prompt is discarded, as readpassphrase does.
            if (tcsetattr(fd, Tcsaflush, quiet) != 0)
            {
                return null;
            }
            var buffer = new byte[maxLength + 1];
            var length = 0;
            try
            {
                var promptBytes = System.Text.Encoding.UTF8.GetBytes(prompt);
                terminal.Write(promptBytes);
                terminal.Flush();
                while (length < buffer.Length)
                {
                    var read = terminal.Read(buffer, length, 1);
                    if (read <= 0 || buffer[length] == (byte)'\n')
                    {
                        break;
                    }
                    length += read;
                }
                if (length > maxLength)
                {
                    throw new UsageException("the text typed is too long");
                }
                return Text.StrictUtf8.GetString(buffer, 0, length).TrimEnd('\r');
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw new UsageException("the pairing code has invalid characters");
            }
            finally
            {
                Restore();
                terminal.Write("\n"u8);
                Array.Clear(buffer);
            }
        }
    }
}
