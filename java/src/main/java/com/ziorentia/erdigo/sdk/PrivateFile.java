package com.ziorentia.erdigo.sdk;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.channels.SeekableByteChannel;
import java.nio.file.AccessDeniedException;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.FileSystems;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.NoSuchFileException;
import java.nio.file.OpenOption;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.nio.file.attribute.AclEntry;
import java.nio.file.attribute.AclEntryFlag;
import java.nio.file.attribute.AclEntryPermission;
import java.nio.file.attribute.AclEntryType;
import java.nio.file.attribute.AclFileAttributeView;
import java.nio.file.attribute.BasicFileAttributes;
import java.nio.file.attribute.FileAttribute;
import java.nio.file.attribute.PosixFileAttributeView;
import java.nio.file.attribute.PosixFilePermission;
import java.nio.file.attribute.PosixFilePermissions;
import java.nio.file.attribute.UserPrincipal;
import java.nio.file.attribute.UserPrincipalLookupService;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.HashSet;
import java.util.List;
import java.util.Objects;
import java.util.Set;
import java.util.UUID;

/**
 * Files and folders that only the current user can read, the way {@code ssh} treats its keys.
 *
 * <p>Where the file system has POSIX permissions (macOS, Linux), a private folder has mode 0700 and a private file mode
 * 0600, and both must belong to the current user. On Windows a private folder gets an access list that grants the
 * current user alone, and an existing folder or file is accepted only if nobody but the current user, SYSTEM and the
 * Administrators group is granted anything in it. Elsewhere nothing is treated as private.
 */
public final class PrivateFile {
    private static final Set<PosixFilePermission> FILE_MODE = EnumSet.of(PosixFilePermission.OWNER_READ, PosixFilePermission.OWNER_WRITE);
    private static final Set<PosixFilePermission> FOLDER_MODE =
            EnumSet.of(PosixFilePermission.OWNER_READ, PosixFilePermission.OWNER_WRITE, PosixFilePermission.OWNER_EXECUTE);

    private PrivateFile() {
    }

    /** Why a file was refused. */
    public enum Problem {
        /** There is no such file. */
        MISSING,
        /** The file exists but could not be opened or read. */
        UNREADABLE,
        /** The file is a link or not a regular file, or someone other than its owner could read or replace it. */
        INSECURE,
        /** The file is larger than the caller allows. */
        TOO_LARGE,
    }

    /** A file {@link PrivateFile} refused. */
    public static final class PrivateFileException extends IOException {
        private static final long serialVersionUID = 1L;

        private final Problem problem;

        public PrivateFileException(Problem problem, String message) {
            super(message);
            this.problem = problem;
        }

        public PrivateFileException(Problem problem, String message, Throwable cause) {
            super(message, cause);
            this.problem = problem;
        }

        public Problem problem() {
            return problem;
        }
    }

    private static boolean isPosix(Path path) {
        return Files.getFileAttributeView(path, PosixFileAttributeView.class, LinkOption.NOFOLLOW_LINKS) != null
                && path.getFileSystem().supportedFileAttributeViews().contains("posix");
    }

    private static boolean hasAcl(Path path) {
        return path.getFileSystem().supportedFileAttributeViews().contains("acl");
    }

    /** True where this class can keep a file private: POSIX permissions, or Windows access lists. */
    public static boolean canProtect(Path path) {
        return isPosix(path) || hasAcl(path);
    }

    /**
     * Reads a whole file that only the current user can read, refusing links, other kinds of file and files over
     * {@code maxBytes}.
     */
    public static byte[] readAllBytes(Path path, int maxBytes) throws PrivateFileException {
        Objects.requireNonNull(path, "path");
        String name = String.valueOf(path.getFileName());
        BasicFileAttributes before;
        try {
            before = Files.readAttributes(path, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
        } catch (NoSuchFileException error) {
            throw new PrivateFileException(Problem.MISSING, name + " does not exist", error);
        } catch (IOException error) {
            throw new PrivateFileException(Problem.UNREADABLE, name + " could not be opened", error);
        }
        if (before.isSymbolicLink() || before.isOther()) {
            throw new PrivateFileException(Problem.INSECURE, name + " must be a regular file, not a link");
        }
        if (!before.isRegularFile()) {
            throw new PrivateFileException(Problem.INSECURE, name + " must be a regular file");
        }
        Set<OpenOption> options = new HashSet<>(List.of(StandardOpenOption.READ, LinkOption.NOFOLLOW_LINKS));
        try (SeekableByteChannel channel = Files.newByteChannel(path, options)) {
            // The file opened is the one that was looked at, not something put in its place since.
            BasicFileAttributes after = Files.readAttributes(path, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
            if (!after.isRegularFile() || !Objects.equals(before.fileKey(), after.fileKey())) {
                throw new PrivateFileException(Problem.INSECURE, name + " changed while it was opened");
            }
            if (!isPrivate(path, false)) {
                throw new PrivateFileException(Problem.INSECURE, isPosix(path)
                        ? name + " must be a regular file owned by you with mode 0600"
                        : name + " must be readable by you alone");
            }
            long size = channel.size();
            if (size > maxBytes) {
                throw new PrivateFileException(Problem.TOO_LARGE, name + " is unexpectedly large");
            }
            ByteBuffer data = ByteBuffer.allocate((int) size);
            while (data.hasRemaining()) {
                if (channel.read(data) < 0) {
                    break;
                }
            }
            if (data.hasRemaining() || channel.read(ByteBuffer.allocate(1)) > 0) {
                throw new PrivateFileException(Problem.TOO_LARGE, name + " changed while it was read");
            }
            return data.array();
        } catch (PrivateFileException error) {
            throw error;
        } catch (NoSuchFileException error) {
            throw new PrivateFileException(Problem.MISSING, name + " does not exist", error);
        } catch (IOException | UnsupportedOperationException error) {
            throw new PrivateFileException(Problem.UNREADABLE, name + " could not be read", error);
        }
    }

    /**
     * Writes a whole file through a temporary file and a rename, so that a crash never leaves a partial file and a
     * reader never sees one. Used inside a private folder.
     *
     * <p>Without {@code overwrite} it fails if anything at all is at the path, even something it cannot read, so a new
     * device key can never take the place of one that exists.
     */
    static void writeAllBytesAtomically(Path path, byte[] data, boolean overwrite) throws IOException {
        Path full = path.toAbsolutePath();
        Path directory = full.getParent();
        if (directory == null) {
            throw new IOException("the file has no folder");
        }
        Path temporary = directory.resolve("." + full.getFileName() + "." + UUID.randomUUID().toString().replace("-", ""));
        boolean succeeded = false;
        try {
            try (FileChannel channel = createPrivate(temporary, StandardOpenOption.CREATE_NEW)) {
                ByteBuffer buffer = ByteBuffer.wrap(data);
                while (buffer.hasRemaining()) {
                    channel.write(buffer);
                }
                channel.force(true);
            }
            replace(temporary, full, overwrite);
            succeeded = true;
        } finally {
            if (!succeeded) {
                try {
                    Files.deleteIfExists(temporary);
                } catch (IOException ignored) {
                    // Left behind; it holds nothing that is not also in the private folder.
                }
            }
        }
    }

    /**
     * Writes a file in place, readable by the current user alone, replacing what it held. Refuses to follow a link. For
     * handing a secret to another program, such as a pairing code.
     */
    public static void writeAllBytes(Path path, byte[] data) throws IOException {
        Objects.requireNonNull(path, "path");
        if (Files.isSymbolicLink(path)) {
            throw new PrivateFileException(Problem.INSECURE, path.getFileName() + " must not be a link");
        }
        try (FileChannel channel = createPrivate(path, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING)) {
            // An existing file keeps its old permissions when it is opened, so they are set again here.
            restrict(path, false);
            ByteBuffer buffer = ByteBuffer.wrap(data);
            while (buffer.hasRemaining()) {
                channel.write(buffer);
            }
            channel.force(true);
        }
    }

    private static FileChannel createPrivate(Path path, OpenOption... extra) throws IOException {
        Set<OpenOption> options = new HashSet<>(List.of(extra));
        options.add(StandardOpenOption.WRITE);
        options.add(LinkOption.NOFOLLOW_LINKS);
        if (isPosix(path.toAbsolutePath().getParent())) {
            FileAttribute<Set<PosixFilePermission>> mode = PosixFilePermissions.asFileAttribute(FILE_MODE);
            return FileChannel.open(path, options, mode);
        }
        FileChannel channel = FileChannel.open(path, options);
        try {
            restrict(path, false);
        } catch (IOException | RuntimeException error) {
            channel.close();
            throw error;
        }
        return channel;
    }

    private static void replace(Path source, Path destination, boolean overwrite) throws IOException {
        if (!overwrite) {
            // A hard link fails if anything is at the destination, which a rename on POSIX would replace.
            try {
                Files.createLink(destination, source);
                Files.delete(source);
                return;
            } catch (FileAlreadyExistsException error) {
                throw error;
            } catch (UnsupportedOperationException | IOException error) {
                if (Files.exists(destination, LinkOption.NOFOLLOW_LINKS)) {
                    throw new FileAlreadyExistsException(destination.toString());
                }
                Files.move(source, destination);
                return;
            }
        }
        // A virus scanner or an indexer can hold a file open for a moment on Windows.
        for (int attempt = 0; ; attempt++) {
            try {
                Files.move(source, destination, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
                return;
            } catch (AtomicMoveNotSupportedException error) {
                Files.move(source, destination, StandardCopyOption.REPLACE_EXISTING);
                return;
            } catch (AccessDeniedException error) {
                if (attempt >= 20 || isPosix(destination.getParent())) {
                    throw error;
                }
                try {
                    Thread.sleep(50);
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                    throw error;
                }
            }
        }
    }

    /** Creates a private folder, or checks that an existing one is private. Its parent folders get default permissions. */
    public static void ensurePrivateDirectory(Path path) throws IOException {
        Path full = path.toAbsolutePath();
        Path parent = full.getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }
        if (!Files.exists(full, LinkOption.NOFOLLOW_LINKS)) {
            try {
                if (isPosix(parent)) {
                    Files.createDirectory(full, PosixFilePermissions.asFileAttribute(FOLDER_MODE));
                } else {
                    Files.createDirectory(full);
                    restrict(full, true);
                }
            } catch (FileAlreadyExistsException error) {
                // Made by someone else at the same moment. It is checked below like any other.
            }
        }
        BasicFileAttributes attributes = Files.readAttributes(full, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
        if (!attributes.isDirectory() || attributes.isSymbolicLink()) {
            throw new PrivateFileException(Problem.INSECURE, "the state folder must be a real folder");
        }
        if (!isPrivate(full, true)) {
            throw new PrivateFileException(Problem.INSECURE, isPosix(full)
                    ? "the state folder must be a real folder owned by you with mode 0700"
                    : "the state folder must be readable by you alone. Remove the other entries from its permissions, or choose another folder");
        }
    }

    /** Gives the path the private permissions: mode 0600 or 0700, or an access list for the current user alone. */
    private static void restrict(Path path, boolean folder) throws IOException {
        if (isPosix(path)) {
            Files.setPosixFilePermissions(path, folder ? FOLDER_MODE : FILE_MODE);
            return;
        }
        AclFileAttributeView view = Files.getFileAttributeView(path, AclFileAttributeView.class, LinkOption.NOFOLLOW_LINKS);
        if (view == null) {
            throw new PrivateFileException(Problem.INSECURE, "this file system cannot keep a file private");
        }
        AclEntry.Builder entry = AclEntry.newBuilder()
                .setType(AclEntryType.ALLOW)
                .setPrincipal(currentUser(path))
                .setPermissions(EnumSet.allOf(AclEntryPermission.class));
        if (folder) {
            entry.setFlags(AclEntryFlag.FILE_INHERIT, AclEntryFlag.DIRECTORY_INHERIT);
        }
        view.setAcl(List.of(entry.build()));
    }

    /** Whether nobody but the current user can read or replace the path. */
    static boolean isPrivate(Path path, boolean folder) throws IOException {
        if (isPosix(path)) {
            Set<PosixFilePermission> mode = Files.getPosixFilePermissions(path, LinkOption.NOFOLLOW_LINKS);
            Set<PosixFilePermission> allowed = folder ? FOLDER_MODE : FILE_MODE;
            for (PosixFilePermission permission : mode) {
                if (!allowed.contains(permission) && permission != PosixFilePermission.OWNER_EXECUTE) {
                    return false;
                }
            }
            // Only the owner (or the superuser) may change a file's mode, so setting it to what it already is shows
            // whether the file is ours, without an API for the owner's user id.
            try {
                Files.setPosixFilePermissions(path, mode);
                return true;
            } catch (IOException | SecurityException error) {
                return false;
            }
        }
        if (hasAcl(path)) {
            AclFileAttributeView view = Files.getFileAttributeView(path, AclFileAttributeView.class, LinkOption.NOFOLLOW_LINKS);
            Set<UserPrincipal> trusted = trustedPrincipals(path);
            for (AclEntry entry : view.getAcl()) {
                if (entry.type() == AclEntryType.ALLOW && !trusted.contains(entry.principal())) {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    private static UserPrincipal currentUser(Path path) throws IOException {
        UserPrincipalLookupService lookup = path.getFileSystem().getUserPrincipalLookupService();
        String name = System.getProperty("user.name");
        if (name == null || name.isEmpty()) {
            throw new PrivateFileException(Problem.UNREADABLE, "the current user could not be determined");
        }
        return lookup.lookupPrincipalByName(name);
    }

    /**
     * The current user, SYSTEM and the Administrators group, which control the computer anyway. Their names are looked
     * up in English; on a Windows in another language a group may not be found, and a file that grants it is then
     * refused, which errs on the safe side.
     */
    private static Set<UserPrincipal> trustedPrincipals(Path path) throws IOException {
        Set<UserPrincipal> trusted = new HashSet<>();
        trusted.add(currentUser(path));
        UserPrincipalLookupService lookup = FileSystems.getDefault().getUserPrincipalLookupService();
        List<String> names = new ArrayList<>(List.of("NT AUTHORITY\\SYSTEM", "SYSTEM", "BUILTIN\\Administrators"));
        for (String name : names) {
            try {
                trusted.add(lookup.lookupPrincipalByName(name));
            } catch (IOException | UnsupportedOperationException notFound) {
                // Not on this system, or not under this name.
            }
        }
        return trusted;
    }
}
