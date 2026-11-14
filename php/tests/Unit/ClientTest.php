<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\Coordinate;
use Erdigo\Core\ItemKind;
use Erdigo\Core\Limits;
use Erdigo\Core\MarkerColor;
use Erdigo\Core\TrackPoint;
use Erdigo\ErdigoClient;
use Erdigo\ErrorKind;
use Erdigo\ReceiverTarget;
use Erdigo\RetryPolicy;
use Erdigo\Tests\Support\EndToEnd;
use PHPUnit\Framework\TestCase;

final class ClientTest extends TestCase
{
    public function testEveryKindOfRequestReachesTheReceiver(): void
    {
        // Points are paced at the receiver's rate, which is one a second for a real one.
        $device = EndToEnd::start(['limits' => ['pointsPerSecond' => 1000, 'pointBurst' => 1000]]);
        $client = $device->client;
        $at = new Coordinate(37.5796, 126.977);
        $client->ping();
        $client->putPin('gate', $at, 'Palace gate');
        $client->putMarker('tower', new Coordinate(48.8584, 2.2945), null, MarkerColor::Blue);
        $client->putNote('meet', $at, "Meet at the gate\nat noon");
        $client->move('gate', new Coordinate(37.58, 126.98));
        $points = [];
        for ($index = 0; $index < 250; $index++) {
            $points[] = new TrackPoint(new Coordinate(37.5 + $index / 1000, 126.9), 1700000000000 + $index * 1000, $index === 0 ? 38.5 : null);
        }
        $client->appendTrack('walk', $points, 'Walk');
        $client->finishTrack('walk');
        $photo = "\xFF\xD8\xFF" . random_bytes(100000);
        $client->putPhoto('view', $at, $photo, 'View');
        $client->delete('meet');
        $client->deleteAll();
        $this->assertSame(1000, $client->limits()?->pointsPerSecond);
        $this->assertSame(Limits::v1()->items, $client->limits()?->items);
        $this->assertSame([ItemKind::Pin, ItemKind::Marker, ItemKind::Note, ItemKind::Photo, ItemKind::Track], $client->allowedKinds());
        $client->close();

        $requests = $device->receiver->requests();
        $types = array_map(static fn (array $request): string => $request['t'] . (isset($request['kind']) ? ":{$request['kind']}" : ''), $requests);
        $this->assertSame([
            'ping', 'put:pin', 'put:marker', 'put:note', 'move', 'track_append', 'track_append', 'track_append', 'track_finish',
            'photo_begin', 'photo_chunk', 'photo_chunk', 'photo_chunk', 'photo_end', 'delete', 'delete_all',
        ], $types);
        $this->assertSame(range(1, 16), $device->receiver->seen());
        $this->assertSame(1, $device->receiver->connections());
        $this->assertSame('Palace gate', $requests[1]['title']);
        $this->assertSame('blue', $requests[2]['color']);
        $this->assertSame("Meet at the gate\nat noon", $requests[3]['text']);
        // Long tracks go in batches of 100, and only the first carries the title.
        $this->assertSame([100, 100, 50], [count($requests[5]['points']), count($requests[6]['points']), count($requests[7]['points'])]);
        $this->assertSame('Walk', $requests[5]['title']);
        $this->assertArrayNotHas('title', $requests[6]);
        $this->assertSame(38.5, $requests[5]['points'][0]['alt']);
        $this->assertSame(1700000249000, $requests[7]['points'][49]['ts']);
        $this->assertSame(['type' => 'image/jpeg', 'size' => 100003, 'sha256' => hash('sha256', $photo), 'chunks' => 3], [
            'type' => $requests[9]['type'], 'size' => $requests[9]['size'], 'sha256' => $requests[9]['sha256'], 'chunks' => $requests[9]['chunks'],
        ]);
        $this->assertSame([0, 1, 2], [$requests[10]['index'], $requests[11]['index'], $requests[12]['index']]);
        $this->assertCount(1, $device->receiver->events('photo_stored'));
    }

    public function testPointsArePacedAtTheReceiversRate(): void
    {
        $device = EndToEnd::start(['limits' => ['pointsPerSecond' => 50, 'pointBurst' => 100]]);
        $points = array_fill(0, 150, new TrackPoint(new Coordinate(1, 2)));
        $started = microtime(true);
        $device->client->appendTrack('walk', $points);
        // The second batch of 50 waits for a second's worth of points.
        $this->assertGreaterThanOrEqual(0.9, microtime(true) - $started);
        $this->assertSame([1, 2], $device->receiver->seen());
    }

    private function assertArrayNotHas(string $key, array $array): void
    {
        $this->assertFalse(array_key_exists($key, $array));
    }

    public function testAKindThePersonDidNotAllowIsRefusedWithoutSendingIt(): void
    {
        $device = EndToEnd::start([], ['pin']);
        $device->client->putPin('a', new Coordinate(1, 2));
        $error = EndToEnd::expect($this, ErrorKind::NotPermitted, static fn () => $device->client->putNote('n', new Coordinate(1, 2), 'x'));
        $this->assertSame(ItemKind::Note, $error->itemKind);
        EndToEnd::expect($this, ErrorKind::NotPermitted, static fn () => $device->client->putPhoto('p', new Coordinate(1, 2), "\xFF\xD8\xFF\xE0"));
        EndToEnd::expect($this, ErrorKind::NotPermitted, static fn () => $device->client->finishTrack('t'));
        $this->assertSame([1], $device->receiver->seen());
    }

    public function testALostConnectionIsResentWithTheSameNumber(): void
    {
        $device = EndToEnd::start(['script' => [['drop' => true]]]);
        $device->client->putPin('a', new Coordinate(1, 2));
        $this->assertSame([1, 1], $device->receiver->seen());
        $this->assertSame(2, $device->receiver->connections());
        $requests = $device->receiver->requests();
        $this->assertSame($requests[0], $requests[1]);
    }

    public function testEachConnectionUsesAGreaterCounter(): void
    {
        $device = EndToEnd::start(['script' => [['drop' => true, 'times' => 2]]]);
        $device->client->ping();
        $counters = array_map(static fn (array $event): int => $event['counter'], $device->receiver->events('connected'));
        $this->assertCount(3, $counters);
        $this->assertTrue($counters[0] < $counters[1] && $counters[1] < $counters[2]);
        $this->assertSame([], $device->receiver->events('silent'));
    }

    public function testNoAnswerOnceTheRetryTimeRunsOut(): void
    {
        $device = EndToEnd::start([], retry: RetryPolicy::upTo(1.5));
        $device->receiver->stop();
        $started = microtime(true);
        EndToEnd::expect($this, ErrorKind::NoAnswer, static fn () => $device->client->ping());
        $elapsed = microtime(true) - $started;
        $this->assertGreaterThanOrEqual(1.4, $elapsed);
        $this->assertLessThan(6.0, $elapsed);
    }

    public function testARequestTimeoutBoundsTheWholeCall(): void
    {
        $device = EndToEnd::start(['script' => [['silent' => true]]], retry: RetryPolicy::forever(), clientOptions: ['requestTimeout' => 1.0]);
        $started = microtime(true);
        EndToEnd::expect($this, ErrorKind::TimedOut, static fn () => $device->client->ping());
        $this->assertLessThan(3.0, microtime(true) - $started);
    }

    public function testAnUnknownDeviceGetsNoAnswer(): void
    {
        $known = EndToEnd::start();
        $stranger = new EndToEnd($known->receiver, retry: RetryPolicy::none());
        EndToEnd::expect($this, ErrorKind::NoAnswer, static fn () => $stranger->client->ping());
        $this->assertSame('unknown device', $known->receiver->events('silent')[0]['why']);
    }

    public function testArgumentsAreCheckedBeforeConnecting(): void
    {
        $device = EndToEnd::start();
        $at = new Coordinate(1, 2);
        $client = $device->client;
        foreach ([
            static fn () => $client->putPin('bad id', $at),
            static fn () => $client->putPin(str_repeat('a', 65), $at),
            static fn () => $client->putPin('a', $at, ''),
            static fn () => $client->putPin('a', $at, "a\u{202E}b"),
            static fn () => $client->putNote('a', $at, ''),
            static fn () => $client->putNote('a', $at, str_repeat('a', 4097)),
            static fn () => $client->appendTrack('a', []),
            static fn () => $client->putPhoto('a', $at, 'GIF89a'),
            static fn () => $client->putPhoto('a', $at, "\xFF\xD8\xFF" . str_repeat('a', 1048574)),
        ] as $call) {
            EndToEnd::expect($this, ErrorKind::InvalidArgument, $call);
        }
        $this->assertSame(0, $device->receiver->connections());
    }

    public function testProcessesSharingAStoreTakeTurns(): void
    {
        $device = EndToEnd::start();
        $device->client->ping();
        // A second client with the same key waits while the first keeps its connection open.
        $second = new ErdigoClient(
            $device->identity,
            $device->pairing,
            $device->store,
            ReceiverTarget::address('127.0.0.1', $device->receiver->port),
            RetryPolicy::none(),
            ackTimeout: 0.5,
        );
        EndToEnd::expect($this, ErrorKind::NoAnswer, static fn () => $second->ping());
        $device->client->close();
        $second->ping();
        $this->assertSame(2, $device->receiver->connections());
    }
}
