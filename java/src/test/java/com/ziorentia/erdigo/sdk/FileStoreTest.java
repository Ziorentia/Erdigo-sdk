package com.ziorentia.erdigo.sdk;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.Noise;
import com.ziorentia.erdigo.sdk.PrivateFile.PrivateFileException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Instant;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

class FileStoreTest {
    @TempDir
    Path temporary;

    private static boolean posix(Path path) {
        return path.getFileSystem().supportedFileAttributeViews().contains("posix");
    }

    private static String mode(Path path) throws Exception {
        return PosixFilePermissions.toString(Files.getPosixFilePermissions(path));
    }

    private static ReceiverPairing pairing(String label) {
        return new ReceiverPairing(label, Noise.publicKey(Noise.newPrivateKey()), List.of(ItemKind.PIN), "192.168.1.5", 4242,
                Instant.parse("2026-11-14T00:00:00Z"), 17);
    }

    @Test
    void keyAndPairingsSurviveARestart() throws Exception {
        Path folder = temporary.resolve("state");
        FileDeviceStore store = new FileDeviceStore(folder);
        String fingerprint;
        try (DeviceIdentity identity = store.loadOrCreateIdentity()) {
            fingerprint = identity.fingerprint();
        }
        ReceiverPairing saved = pairing("home");
        store.save(saved);
        long counter = store.nextConnectCounter(saved.fingerprint(), 5);
        assertEquals(18, counter);

        FileDeviceStore again = new FileDeviceStore(folder);
        try (DeviceIdentity identity = again.loadOrCreateIdentity()) {
            assertEquals(fingerprint, identity.fingerprint());
        }
        assertEquals(List.of(saved), again.pairings());
        assertEquals(19, again.nextConnectCounter(saved.fingerprint(), 5));
        assertEquals(1_700_000_000_000L, again.nextConnectCounter(saved.fingerprint(), 1_700_000_000_000L));
        again.updateAddress(saved.fingerprint(), "10.0.0.2", 99);
        assertEquals("10.0.0.2", again.pairings().get(0).lastHost());
        again.removePairing(saved.fingerprint());
        assertEquals(List.of(), again.pairings());
        // A pairing saved again starts from its floor, not from what was forgotten.
        again.save(saved);
        assertEquals(18, again.nextConnectCounter(saved.fingerprint(), 0));
    }

    @Test
    void folderAndFilesAreReadableByTheOwnerOnly() throws Exception {
        assumeTrue(posix(temporary));
        Path folder = temporary.resolve("state");
        FileDeviceStore store = new FileDeviceStore(folder);
        store.loadOrCreateIdentity().close();
        store.save(pairing("home"));
        assertEquals("rwx------", mode(folder));
        assertEquals("rw-------", mode(folder.resolve("identity.key")));
        assertEquals("rw-------", mode(folder.resolve("pairings.json")));
    }

    @Test
    void aFolderOthersCanReadIsRefused() throws Exception {
        assumeTrue(posix(temporary));
        Path folder = temporary.resolve("open");
        Files.createDirectory(folder, PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rwxr-xr-x")));
        ErdigoException error = assertThrows(ErdigoException.class, () -> new FileDeviceStore(folder));
        assertEquals(ErdigoException.StorageProblem.INSECURE, error.storageProblem());
    }

    @Test
    void aKeyFileOthersCanReadIsNotRead() throws Exception {
        assumeTrue(posix(temporary));
        Path folder = temporary.resolve("state");
        FileDeviceStore store = new FileDeviceStore(folder);
        store.loadOrCreateIdentity().close();
        Files.setPosixFilePermissions(folder.resolve("identity.key"), PosixFilePermissions.fromString("rw-r--r--"));
        ErdigoException error = assertThrows(ErdigoException.class, store::loadOrCreateIdentity);
        assertEquals(ErdigoException.StorageProblem.INSECURE, error.storageProblem());
    }

    @Test
    void aDamagedKeyIsReportedAndNeverReplaced() throws Exception {
        Path folder = temporary.resolve("state");
        FileDeviceStore store = new FileDeviceStore(folder);
        store.loadOrCreateIdentity().close();
        Path key = folder.resolve("identity.key");
        byte[] damaged = "not a key".getBytes(StandardCharsets.US_ASCII);
        Files.write(key, damaged);
        ErdigoException error = assertThrows(ErdigoException.class, store::loadOrCreateIdentity);
        assertEquals(ErdigoException.StorageProblem.IDENTITY_DAMAGED, error.storageProblem());
        assertArrayEquals(damaged, Files.readAllBytes(key));
    }

    @Test
    void aLinkInPlaceOfTheKeyIsRefusedAndNotReplaced() throws Exception {
        assumeTrue(posix(temporary));
        Path folder = temporary.resolve("state");
        FileDeviceStore store = new FileDeviceStore(folder);
        Path elsewhere = temporary.resolve("elsewhere.key");
        Files.write(elsewhere, new byte[36]);
        Files.createSymbolicLink(folder.resolve("identity.key"), elsewhere);
        ErdigoException error = assertThrows(ErdigoException.class, store::loadOrCreateIdentity);
        assertEquals(ErdigoException.StorageProblem.INSECURE, error.storageProblem());
        assertArrayEquals(new byte[36], Files.readAllBytes(elsewhere));
    }

    @Test
    void damagedPairingsAreReported() throws Exception {
        Path folder = temporary.resolve("state");
        FileDeviceStore store = new FileDeviceStore(folder);
        store.save(pairing("home"));
        Files.write(folder.resolve("pairings.json"), "{\"version\":1,\"pairings\":[{}],\"counters\":{}}".getBytes(StandardCharsets.UTF_8));
        assertEquals(ErdigoException.StorageProblem.STATE_DAMAGED, assertThrows(ErdigoException.class, store::pairings).storageProblem());
    }

    @Test
    void privateFilesAreReadOnlyWhenPrivate() throws Exception {
        assumeTrue(posix(temporary));
        Path file = temporary.resolve("code");
        PrivateFile.writeAllBytes(file, "secret".getBytes(StandardCharsets.US_ASCII));
        assertEquals("rw-------", mode(file));
        assertEquals("secret", new String(PrivateFile.readAllBytes(file, 1024), StandardCharsets.US_ASCII));
        assertEquals(PrivateFile.Problem.TOO_LARGE, assertThrows(PrivateFileException.class, () -> PrivateFile.readAllBytes(file, 3)).problem());
        Files.setPosixFilePermissions(file, PosixFilePermissions.fromString("rw-r-----"));
        assertEquals(PrivateFile.Problem.INSECURE, assertThrows(PrivateFileException.class, () -> PrivateFile.readAllBytes(file, 1024)).problem());
        // Writing it again makes it private again.
        PrivateFile.writeAllBytes(file, "new".getBytes(StandardCharsets.US_ASCII));
        assertEquals("rw-------", mode(file));
        assertEquals(PrivateFile.Problem.MISSING,
                assertThrows(PrivateFileException.class, () -> PrivateFile.readAllBytes(temporary.resolve("none"), 10)).problem());
    }

    /** On Windows the folder gets an access list for the current user alone, and a file in it is private. */
    @Test
    void windowsAccessListGrantsTheCurrentUserAlone() throws Exception {
        assumeTrue(!posix(temporary) && temporary.getFileSystem().supportedFileAttributeViews().contains("acl"));
        Path folder = temporary.resolve("state");
        FileDeviceStore store = new FileDeviceStore(folder);
        store.loadOrCreateIdentity().close();
        java.nio.file.attribute.AclFileAttributeView view =
                Files.getFileAttributeView(folder, java.nio.file.attribute.AclFileAttributeView.class);
        java.nio.file.attribute.UserPrincipal user =
                folder.getFileSystem().getUserPrincipalLookupService().lookupPrincipalByName(System.getProperty("user.name"));
        for (java.nio.file.attribute.AclEntry entry : view.getAcl()) {
            assertEquals(user, entry.principal());
        }
        assertFalse(view.getAcl().isEmpty());
        PrivateFile.readAllBytes(folder.resolve("identity.key"), 1024);
    }
}
