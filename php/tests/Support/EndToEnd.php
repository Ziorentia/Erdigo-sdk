<?php

declare(strict_types=1);

namespace Erdigo\Tests\Support;

use Erdigo\Core\ItemKind;
use Erdigo\DeviceIdentity;
use Erdigo\ErdigoClient;
use Erdigo\ErdigoException;
use Erdigo\ErrorKind;
use Erdigo\FileDeviceStore;
use Erdigo\ReceiverPairing;
use Erdigo\ReceiverTarget;
use Erdigo\RetryPolicy;
use PHPUnit\Framework\TestCase;

/**
 * A device already paired with a scripted receiver.
 */
final class EndToEnd
{
    public readonly FileDeviceStore $store;
    public readonly DeviceIdentity $identity;
    public readonly ReceiverPairing $pairing;
    public readonly ErdigoClient $client;

    /**
     * @param list<string> $kinds What the receiver allows this device.
     * @param array<string, mixed> $clientOptions Named arguments for the client.
     */
    public function __construct(
        public readonly ScriptedReceiver $receiver,
        array $kinds = ['pin', 'marker', 'note', 'photo', 'track'],
        ?RetryPolicy $retry = null,
        array $clientOptions = [],
        ?FileDeviceStore $store = null,
    ) {
        $this->store = $store ?? new FileDeviceStore(Temporary::directory() . '/state');
        $this->identity = $this->store->loadOrCreateIdentity();
        $this->pairing = new ReceiverPairing(
            'test',
            $receiver->publicKey,
            array_map(static fn (string $kind): ItemKind => ItemKind::from($kind), $kinds),
            '127.0.0.1',
            $receiver->port,
        );
        $this->store->save($this->pairing);
        $this->client = new ErdigoClient(
            $this->identity,
            $this->pairing,
            $this->store,
            ReceiverTarget::address('127.0.0.1', $receiver->port),
            $retry ?? RetryPolicy::upTo(10.0),
            ...$clientOptions,
        );
    }

    /**
     * Starts a receiver that knows this device, with the given settings.
     *
     * @param array<string, mixed> $settings
     * @param list<string> $kinds
     * @param array<string, mixed> $clientOptions
     */
    public static function start(array $settings = [], array $kinds = ['pin', 'marker', 'note', 'photo', 'track'], ?RetryPolicy $retry = null, array $clientOptions = []): self
    {
        $store = new FileDeviceStore(Temporary::directory() . '/state');
        $identity = $store->loadOrCreateIdentity();
        $settings['devices'] = [['public' => bin2hex($identity->publicKey), 'kinds' => $kinds]];
        return new self(new ScriptedReceiver($settings), $kinds, $retry, $clientOptions, $store);
    }

    public static function expect(TestCase $test, ErrorKind $kind, callable $body): ErdigoException
    {
        try {
            $body();
        } catch (ErdigoException $error) {
            TestCase::assertSame($kind, $error->kind, $error->getMessage());
            return $error;
        }
        TestCase::fail("expected {$kind->value}");
    }
}
