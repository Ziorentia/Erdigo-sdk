<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\ItemKind;
use Erdigo\Core\Noise\Noise;
use Erdigo\ErdigoException;
use Erdigo\ErrorKind;
use Erdigo\FileDeviceStore;
use Erdigo\PrivateFile;
use Erdigo\ReceiverPairing;
use Erdigo\StorageProblem;
use Erdigo\Tests\Support\Temporary;
use PHPUnit\Framework\TestCase;

final class FileStoreTest extends TestCase
{
    private ?string $documentRoot = null;

    protected function setUp(): void
    {
        $this->documentRoot = $_SERVER['DOCUMENT_ROOT'] ?? null;
    }

    protected function tearDown(): void
    {
        if ($this->documentRoot === null) {
            unset($_SERVER['DOCUMENT_ROOT']);
        } else {
            $_SERVER['DOCUMENT_ROOT'] = $this->documentRoot;
        }
    }

    private static function modeOf(string $path): int
    {
        clearstatcache(true, $path);
        return fileperms($path) & 0777;
    }

    private function expectStorage(StorageProblem $problem, callable $body): ErdigoException
    {
        try {
            $body();
        } catch (ErdigoException $error) {
            $this->assertSame(ErrorKind::Storage, $error->kind, $error->getMessage());
            $this->assertSame($problem, $error->storageProblem, $error->getMessage());
            return $error;
        }
        $this->fail('expected a storage error');
    }

    private static function samplePairing(int $counterFloor = 0): ReceiverPairing
    {
        return new ReceiverPairing('home', Noise::publicKey(Noise::newPrivateKey()), [ItemKind::Pin, ItemKind::Track], counterFloor: $counterFloor);
    }

    public function testTheKeyIsCreatedOnceAndKeptPrivate(): void
    {
        $directory = Temporary::directory() . '/state';
        $store = new FileDeviceStore($directory);
        $identity = $store->loadOrCreateIdentity();
        $again = (new FileDeviceStore($directory))->loadOrCreateIdentity();
        $this->assertSame($identity->fingerprint, $again->fingerprint);
        $this->assertSame(36, filesize($directory . '/identity.key'));
        $store->save(self::samplePairing());
        if (!PrivateFile::isWindows()) {
            $this->assertSame(0700, self::modeOf($directory));
            $this->assertSame(0600, self::modeOf($directory . '/identity.key'));
            $this->assertSame(0600, self::modeOf($directory . '/pairings.json'));
        }
    }

    public function testPairingsAndCountersArePersisted(): void
    {
        $directory = Temporary::directory() . '/state';
        $store = new FileDeviceStore($directory);
        $pairing = self::samplePairing(5000);
        $store->save($pairing);
        $fingerprint = $pairing->fingerprint();
        // The receiver's floor comes first, then the clock, then one more than the last value.
        $this->assertSame(5001, $store->nextConnectCounter($fingerprint, 10));
        $this->assertSame(9000, $store->nextConnectCounter($fingerprint, 9000));
        $this->assertSame(9001, $store->nextConnectCounter($fingerprint, 100));
        $reopened = new FileDeviceStore($directory);
        $this->assertSame(9002, $reopened->nextConnectCounter($fingerprint, 100));
        $store->updateAddress($fingerprint, '192.168.1.20', 52345);
        $stored = $reopened->pairings();
        $this->assertCount(1, $stored);
        $this->assertSame('home', $stored[0]->label);
        $this->assertSame($pairing->receiverKey, $stored[0]->receiverKey);
        $this->assertSame([ItemKind::Pin, ItemKind::Track], $stored[0]->kinds);
        $this->assertSame('192.168.1.20', $stored[0]->lastHost);
        $this->assertSame(52345, $stored[0]->lastPort);
        $this->assertSame(5000, $stored[0]->counterFloor);
        $this->assertSame($pairing->pairedAt->format('U.u'), $stored[0]->pairedAt->format('U.u'));
        $store->removePairing($fingerprint);
        $this->assertSame([], $reopened->pairings());
        $this->assertSame(100, $reopened->nextConnectCounter($fingerprint, 100));
    }

    public function testADamagedKeyIsReportedAndNeverReplaced(): void
    {
        $directory = Temporary::directory() . '/state';
        $store = new FileDeviceStore($directory);
        file_put_contents($directory . '/identity.key', 'not a key');
        chmod($directory . '/identity.key', 0600);
        $this->expectStorage(StorageProblem::IdentityDamaged, static fn () => $store->loadOrCreateIdentity());
        $this->assertSame('not a key', file_get_contents($directory . '/identity.key'));
    }

    public function testADamagedPairingFileIsReported(): void
    {
        $directory = Temporary::directory() . '/state';
        $store = new FileDeviceStore($directory);
        foreach (['{', '{"version":2,"pairings":[],"counters":{}}', '{"version":1,"pairings":[{"label":"x"}],"counters":{}}'] as $text) {
            file_put_contents($directory . '/pairings.json', $text);
            chmod($directory . '/pairings.json', 0600);
            $this->expectStorage(StorageProblem::StateDamaged, static fn () => $store->pairings());
        }
    }

    public function testFilesOthersCouldReadAreRefused(): void
    {
        if (PrivateFile::isWindows()) {
            $this->markTestSkipped('Windows uses access lists; see testWindowsFoldersGrantOnlyTheUser');
        }
        $directory = Temporary::directory() . '/state';
        $store = new FileDeviceStore($directory);
        $store->loadOrCreateIdentity();
        chmod($directory . '/identity.key', 0644);
        $this->expectStorage(StorageProblem::Insecure, static fn () => $store->loadOrCreateIdentity());
        chmod($directory . '/identity.key', 0600);
        $store->loadOrCreateIdentity();

        // A link in place of the key is refused, wherever it points.
        $elsewhere = Temporary::directory() . '/key';
        rename($directory . '/identity.key', $elsewhere);
        symlink($elsewhere, $directory . '/identity.key');
        $this->expectStorage(StorageProblem::Insecure, static fn () => $store->loadOrCreateIdentity());

        $open = Temporary::directory() . '/open';
        mkdir($open, 0755);
        chmod($open, 0755);
        $this->expectStorage(StorageProblem::Insecure, static fn () => new FileDeviceStore($open));
    }

    public function testAFolderInsideTheDocumentRootIsRefused(): void
    {
        $root = Temporary::directory();
        $_SERVER['DOCUMENT_ROOT'] = $root;
        $error = $this->expectStorage(StorageProblem::Insecure, static fn () => new FileDeviceStore($root . '/private/erdigo'));
        $this->assertStringContainsString('document root', $error->getMessage());
        $this->expectStorage(StorageProblem::Insecure, static fn () => new FileDeviceStore($root . '/a/../erdigo'));
        $this->expectStorage(StorageProblem::Insecure, static fn () => new FileDeviceStore($root));
        $this->assertFileDoesNotExist($root . '/erdigo');
        // Through a link from outside, too.
        if (!PrivateFile::isWindows()) {
            $outside = Temporary::directory();
            symlink($root, $outside . '/link');
            $this->expectStorage(StorageProblem::Insecure, static fn () => new FileDeviceStore($outside . '/link/erdigo'));
        }
        // A folder next to it is fine.
        new FileDeviceStore(dirname($root) . '/' . basename($root) . '-state/erdigo');
        Temporary::adopt(dirname($root) . '/' . basename($root) . '-state');
    }

    public function testConnectionLocksExcludeEachOther(): void
    {
        $store = new FileDeviceStore(Temporary::directory() . '/state');
        $fingerprint = self::samplePairing()->fingerprint();
        $first = $store->acquireConnectionLock($fingerprint, 1.0);
        $this->assertNotNull($first);
        $started = microtime(true);
        $this->assertNull($store->acquireConnectionLock($fingerprint, 0.3));
        $this->assertGreaterThanOrEqual(0.25, microtime(true) - $started);
        $first->release();
        $second = $store->acquireConnectionLock($fingerprint, 0.3);
        $this->assertNotNull($second);
        $second->release();
    }

    public function testWindowsFoldersGrantOnlyTheUser(): void
    {
        if (!PrivateFile::isWindows()) {
            $this->markTestSkipped('only on Windows');
        }
        // What the tools report, so that a failure shows why.
        $probe = Temporary::directory() . '\\probe';
        mkdir($probe);
        \Erdigo\WindowsAcl::makePrivate($probe);
        $read = new \ReflectionMethod(\Erdigo\WindowsAcl::class, 'sddl');
        $sddl = (string) $read->invoke(null, $probe);
        $sid = \Erdigo\WindowsAcl::currentUserSid();
        $this->assertTrue(\Erdigo\WindowsAcl::grantsOnly($sddl, $sid), "access list '{$sddl}' for user '{$sid}'");

        $directory = Temporary::directory() . '\\state';
        $store = new FileDeviceStore($directory);
        $store->loadOrCreateIdentity();
        $this->assertTrue(\Erdigo\WindowsAcl::isPrivate($directory));
        $this->assertTrue(\Erdigo\WindowsAcl::isPrivate($directory . '\\identity.key'));
        // Everyone may read: refused.
        exec('icacls ' . escapeshellarg($directory . '\\identity.key') . ' /grant *S-1-1-0:R /q', $output, $status);
        $this->assertSame(0, $status);
        $this->expectStorage(StorageProblem::Insecure, static fn () => $store->loadOrCreateIdentity());
        exec('icacls ' . escapeshellarg($directory) . ' /grant *S-1-1-0:(OI)(CI)R /q', $output, $status);
        $this->expectStorage(StorageProblem::Insecure, static fn () => new FileDeviceStore($directory));
    }
}
