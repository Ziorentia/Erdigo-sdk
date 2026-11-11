using System;
using System.Net.Sockets;

namespace Erdigo.Core;

/// <summary>Socket options that .NET does not spell the same way on every system.</summary>
internal static class SocketReuse
{
    /// <summary>
    /// Lets a listener take a port that a stopped listener used a moment ago (SO_REUSEADDR only). Windows allows that
    /// already, and there the same option would let another program share the port, so it is left alone.
    /// </summary>
    public static void AllowRebinding(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var (level, reuseAddress, _) = Constants();
        socket.SetRawSocketOption(level, reuseAddress, BitConverter.GetBytes(1));
    }

    /// <summary>Lets several programs listen on one UDP port, as multicast DNS needs.</summary>
    public static void AllowSharing(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            return;
        }
        var (level, reuseAddress, reusePort) = Constants();
        socket.SetRawSocketOption(level, reuseAddress, BitConverter.GetBytes(1));
        socket.SetRawSocketOption(level, reusePort, BitConverter.GetBytes(1));
    }

    /// <summary>SOL_SOCKET, SO_REUSEADDR and SO_REUSEPORT for Linux, and for macOS and the BSDs.</summary>
    private static (int Level, int ReuseAddress, int ReusePort) Constants() =>
        OperatingSystem.IsLinux() ? (1, 2, 15) : (0xFFFF, 0x0004, 0x0200);
}
