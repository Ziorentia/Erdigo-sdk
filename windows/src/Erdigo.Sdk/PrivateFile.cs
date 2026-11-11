using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Erdigo.Sdk;

/// <summary>Why <see cref="PrivateFile"/> refused a file.</summary>
public enum PrivateFileProblem
{
    /// <summary>There is no such file.</summary>
    Missing,

    /// <summary>The file exists but could not be opened or read.</summary>
    Unreadable,

    /// <summary>The file is a link or not a regular file, or someone other than its owner could read or replace it.</summary>
    Insecure,

    /// <summary>The file is larger than the caller allows.</summary>
    TooLarge,
}

public sealed class PrivateFileException : IOException
{
    public PrivateFileException(PrivateFileProblem problem, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problem = problem;
    }

    public PrivateFileProblem Problem { get; }
}

/// <summary>
/// Files and folders that only the current user can read, the way <c>ssh</c> treats its keys.
/// </summary>
/// <remarks>
/// On Windows a private folder has a protected access list that grants the current user alone, and an existing one
/// is accepted only if nobody but the current user, SYSTEM and the Administrators group is granted anything in it.
/// Elsewhere a private folder has mode 0700 and a private file mode 0600, and both must belong to the current user.
/// </remarks>
public static class PrivateFile
{
    /// <summary>
    /// Reads a whole file that only the current user can read, refusing links, other kinds of file and files
    /// over <paramref name="maxBytes"/>.
    /// </summary>
    public static byte[] ReadAllBytes(string path, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            if (Directory.Exists(path))
            {
                throw new PrivateFileException(PrivateFileProblem.Insecure, $"{info.Name} must be a regular file");
            }
            if (info.LinkTarget is not null)
            {
                throw new PrivateFileException(PrivateFileProblem.Insecure, $"{info.Name} must be a regular file, not a link");
            }
            throw new PrivateFileException(PrivateFileProblem.Missing, $"{info.Name} does not exist");
        }
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PrivateFileException(PrivateFileProblem.Insecure, $"{info.Name} must be a regular file, not a link");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PrivateFileException(PrivateFileProblem.Missing, $"{info.Name} does not exist", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PrivateFileException(PrivateFileProblem.Unreadable, $"{info.Name} could not be opened", error);
        }

        using (stream)
        {
            if (OperatingSystem.IsWindows())
            {
                if (!HasPrivateAccessList(info.GetAccessControl()))
                {
                    throw new PrivateFileException(PrivateFileProblem.Insecure, $"{info.Name} must be readable by you alone");
                }
            }
            else if (!IsPrivateUnixFile(stream))
            {
                throw new PrivateFileException(PrivateFileProblem.Insecure, $"{info.Name} must be a regular file owned by you with mode 0600");
            }

            try
            {
                if (stream.Length > maxBytes)
                {
                    throw new PrivateFileException(PrivateFileProblem.TooLarge, $"{info.Name} is unexpectedly large");
                }
                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                if (stream.ReadByte() != -1)
                {
                    throw new PrivateFileException(PrivateFileProblem.TooLarge, $"{info.Name} changed while it was read");
                }
                return data;
            }
            catch (Exception error) when (error is IOException and not PrivateFileException or UnauthorizedAccessException)
            {
                throw new PrivateFileException(PrivateFileProblem.Unreadable, $"{info.Name} could not be read", error);
            }
        }
    }

    /// <summary>
    /// Writes a whole file through a temporary file and a rename, so that a crash never leaves a partial file and a
    /// reader never sees one. Used inside a private folder, whose permissions the file takes on.
    /// </summary>
    /// <remarks>
    /// Without <paramref name="overwrite"/> it fails if anything at all is at the path, even something it cannot
    /// read, so a new device key can never take the place of one that exists.
    /// </remarks>
    internal static void WriteAllBytesAtomically(string path, ReadOnlySpan<byte> data, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? throw new IOException("the file has no folder");
        var name = Path.GetFileName(full);
        var temporary = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}");
        var succeeded = false;
        try
        {
            using (var stream = CreatePrivate(temporary, FileMode.CreateNew))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            Replace(temporary, full, overwrite);
            succeeded = true;
        }
        finally
        {
            if (!succeeded)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Writes a file in place, readable by the current user alone, replacing what it held. Refuses to follow a link.
    /// For handing a secret to another program, such as a pairing code.
    /// </summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(path);
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new PrivateFileException(PrivateFileProblem.Insecure, $"{info.Name} must not be a link");
        }
        using var stream = CreatePrivate(path, FileMode.Create);
        // An existing file keeps its old permissions when it is opened, so they are set again here.
        if (OperatingSystem.IsWindows())
        {
            info.SetAccessControl(PrivateFileSecurity());
        }
        else
        {
            File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream CreatePrivate(string path, FileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            // In a private folder a new file inherits the folder's access list. WriteAllBytes sets its own.
            return new FileStream(path, mode, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    private static void Replace(string source, string destination, bool overwrite)
    {
        // A virus scanner or an indexer can hold a file open for a moment on Windows.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException) when (overwrite && OperatingSystem.IsWindows() && attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Creates a private folder, or checks that an existing one is private. Its parent folders get default permissions.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }
        var info = new DirectoryInfo(full);
        if (!info.Exists && info.LinkTarget is null && !File.Exists(full))
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    info.Create(PrivateDirectorySecurity());
                }
                else
                {
                    Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch (IOException) when (Directory.Exists(full))
            {
                // Created by someone else at the same moment. It is checked below like any other.
            }
        }
        CheckPrivateDirectory(full);
    }

    private static void CheckPrivateDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PrivateFileException(PrivateFileProblem.Insecure, "the state folder must be a real folder");
        }
        if (OperatingSystem.IsWindows())
        {
            if (!HasPrivateAccessList(info.GetAccessControl()))
            {
                throw new PrivateFileException(
                    PrivateFileProblem.Insecure,
                    "the state folder must be readable by you alone. Remove the other entries from its permissions, or choose another folder");
            }
            return;
        }
        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode)0b000_111_111) != 0 || !IsOwnedByCurrentUser(path, mode))
        {
            throw new PrivateFileException(PrivateFileProblem.Insecure, "the state folder must be a real folder owned by you with mode 0700");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static bool IsPrivateUnixFile(FileStream stream)
    {
        var mode = File.GetUnixFileMode(stream.SafeFileHandle);
        if ((mode & (UnixFileMode)0b000_111_111) != 0)
        {
            return false;
        }
        // Only the owner (or the superuser) may change a file's mode, so setting it to what it already is shows
        // whether the file is ours, without an API for the owner's user id.
        try
        {
            File.SetUnixFileMode(stream.SafeFileHandle, mode);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static bool IsOwnedByCurrentUser(string path, UnixFileMode mode)
    {
        try
        {
            File.SetUnixFileMode(path, mode);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new PrivateFileException(PrivateFileProblem.Unreadable, "the current Windows user could not be determined");
    }

    /// <summary>Only the current user, with inheritance to everything inside, and nothing inherited from above.</summary>
    [SupportedOSPlatform("windows")]
    private static DirectorySecurity PrivateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        var user = CurrentUser();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity PrivateFileSecurity()
    {
        var security = new FileSecurity();
        var user = CurrentUser();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// True if nobody but the current user, SYSTEM and the Administrators group is granted anything, on the object
    /// itself or on what it passes on to new files. SYSTEM and administrators control the computer anyway.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool HasPrivateAccessList(FileSystemSecurity security)
    {
        var user = CurrentUser();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid)
            {
                continue;
            }
            var trusted = sid.Equals(user)
                || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            if (!trusted)
            {
                return false;
            }
        }
        return true;
    }
}
