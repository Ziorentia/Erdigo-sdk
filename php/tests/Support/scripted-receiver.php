<?php

declare(strict_types=1);

/*
 * A receiver for the tests, run in a process of its own. It follows the protocol well enough to pair and to take
 * requests, and lets a test replace its answers, so that the client's handling of every answer can be checked over
 * real TCP. It handles one connection at a time and writes what happened to a log, one JSON object per line. The file
 * named by `silence` holds how many of the next connections to close unanswered; a test may change it at any time.
 *
 *     php scripted-receiver.php CONFIG_FILE
 *
 * It prints "port N" once it listens, and runs until it is stopped. It is a test tool, not a receiver.
 */

require __DIR__ . '/../../autoload.php';

use Erdigo\Core\FrameStream;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\Limits;
use Erdigo\Core\Message\Ack;
use Erdigo\Core\Message\PairStatus;
use Erdigo\Core\Message\PhotoBegin;
use Erdigo\Core\Message\PhotoChunk;
use Erdigo\Core\Message\PhotoEnd;
use Erdigo\Core\Message\ReceiverError;
use Erdigo\Core\Message\Welcome;
use Erdigo\Core\Message\Wire;
use Erdigo\Core\Message\PutPin;
use Erdigo\Core\Message\PutMarker;
use Erdigo\Core\Message\PutNote;
use Erdigo\Core\Message\TrackAppend;
use Erdigo\Core\Message\TrackFinish;
use Erdigo\Core\Noise\HandshakeState;
use Erdigo\Core\Noise\Noise;
use Erdigo\Core\Noise\Pattern;
use Erdigo\Core\PairingCode;
use Erdigo\Core\WireException;

$config = json_decode((string) file_get_contents($argv[1]), true, 64, JSON_THROW_ON_ERROR);
$log = fopen($config['log'], 'a');
$note = static function (array $event) use ($log): void {
    fwrite($log, json_encode($event, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE) . "\n");
    fflush($log);
};

$receiverPrivate = hex2bin($config['receiver_private']);
/** @var array<string, array{kinds: list<string>, last_seq: int, counter: int}> $devices Keyed by the device's public key in hex. */
$devices = [];
foreach ($config['devices'] ?? [] as $device) {
    $devices[$device['public']] = ['kinds' => $device['kinds'], 'last_seq' => $device['last_seq'] ?? 0, 'counter' => 0];
}
$script = $config['script'] ?? [];
$requestCount = 0;

$server = stream_socket_server('tcp://127.0.0.1:0', $errorNumber, $errorText);
if ($server === false) {
    fwrite(STDERR, "could not listen: {$errorText}\n");
    exit(1);
}
$name = (string) stream_socket_get_name($server, false);
echo 'port ' . substr($name, strrpos($name, ':') + 1) . "\n";
fflush(STDOUT);

/** The next scripted answer, if the script says anything about this request. */
$scripted = static function () use (&$script, &$requestCount): ?array {
    $index = $requestCount;
    $requestCount += 1;
    $position = 0;
    foreach ($script as $entry) {
        $times = $entry['times'] ?? 1;
        if ($index < $position + $times) {
            return $entry;
        }
        $position += $times;
    }
    return null;
};

/** Whether to close this connection unanswered, because the test asked for that. */
$silenced = static function () use ($config): bool {
    $left = (int) @file_get_contents($config['silence']);
    if ($left <= 0) {
        return false;
    }
    file_put_contents($config['silence'] . '.left', (string) ($left - 1));
    rename($config['silence'] . '.left', $config['silence']);
    return true;
};

$limits = Limits::v1();
if (isset($config['limits'])) {
    $values = array_merge(get_object_vars($limits), $config['limits']);
    $limits = new Limits(...array_values($values));
}

while (true) {
    $socket = @stream_socket_accept($server, 3600);
    if ($socket === false) {
        continue;
    }
    if ($silenced()) {
        $note(['event' => 'silent', 'why' => 'told to']);
        fclose($socket);
        continue;
    }
    $stream = FrameStream::fromAccepted($socket);
    $stream->armDeadline(10.0);
    try {
        $preamble = $stream->readExactly(5);
        $mode = IntegrationV1::parsePreamble($preamble);
        if ($mode === IntegrationV1::MODE_PAIR) {
            $note(['event' => 'pair_connection']);
            $pairing = $config['pairing'] ?? null;
            if ($pairing === null) {
                $stream->close();
                continue;
            }
            $psk = (new PairingCode($pairing['code']))->presharedKey();
            $handshake = HandshakeState::start(Pattern::xxPsk0(), false, $preamble, $receiverPrivate, null, $psk);
            try {
                $handshake->readMessage($stream->readFrame(IntegrationV1::MAX_HANDSHAKE_FRAME_LENGTH));
            } catch (Throwable) {
                $note(['event' => 'silent', 'why' => 'wrong code']);
                $stream->close();
                continue;
            }
            $stream->sendFrame($handshake->writeMessage());
            $note(['event' => 'message2_sent']);
            try {
                $payload = $handshake->readMessage($stream->readFrame(IntegrationV1::MAX_HANDSHAKE_FRAME_LENGTH));
            } catch (Throwable) {
                $note(['event' => 'ended_after_message2']);
                $stream->close();
                continue;
            }
            $request = Wire::decodePairRequest($payload);
            $devicePublic = (string) $handshake->remoteStaticKey();
            $transport = $handshake->split();
            $note([
                'event' => 'pair_request',
                'name' => $request->name,
                'sdk' => $request->sdk,
                'kinds' => array_map(static fn (ItemKind $kind): string => $kind->value, $request->kinds),
                'device' => bin2hex($devicePublic),
                'canonical' => Noise::isCanonicalPublicKey($devicePublic),
            ]);
            $stream->armDeadline(10.0);
            $stream->sendFrame($transport->seal(Wire::encodeReceiverMessage(PairStatus::pending())));
            switch ($pairing['decision'] ?? 'approve') {
                case 'approve':
                    $allowed = array_values(array_filter(
                        $request->kinds,
                        static fn (ItemKind $kind): bool => in_array($kind->value, $pairing['kinds'] ?? ['pin', 'marker', 'note', 'photo', 'track'], true),
                    ));
                    $counter = $pairing['counter'] ?? 0;
                    $devices[bin2hex($devicePublic)] = [
                        'kinds' => array_map(static fn (ItemKind $kind): string => $kind->value, $allowed),
                        'last_seq' => 0,
                        'counter' => $counter,
                    ];
                    usleep((int) (($pairing['delay'] ?? 0) * 1e6));
                    $stream->sendFrame($transport->seal(Wire::encodeReceiverMessage(PairStatus::approved($allowed, $counter))));
                    break;
                case 'deny':
                    $stream->sendFrame($transport->seal(Wire::encodeReceiverMessage(PairStatus::denied())));
                    break;
                case 'code_reused':
                    $stream->sendFrame($transport->seal(Wire::encodeReceiverMessage(ReceiverError::of(\Erdigo\Core\ErrorCode::CodeReused, fatal: true))));
                    break;
                default:
                    // Nobody answers.
                    sleep(30);
            }
            $stream->finishSending();
            usleep(100000);
            $stream->close();
            continue;
        }
        if ($mode !== IntegrationV1::MODE_CONNECT) {
            $stream->close();
            continue;
        }
        $handshake = HandshakeState::start(Pattern::ik(), false, $preamble, $receiverPrivate);
        try {
            $first = $stream->readFrame(IntegrationV1::MAX_HANDSHAKE_FRAME_LENGTH);
            if (strlen($first) !== IntegrationV1::CONNECT_FIRST_MESSAGE_LENGTH) {
                throw new RuntimeException('wrong length');
            }
            $counterBytes = $handshake->readMessage($first);
        } catch (Throwable) {
            $note(['event' => 'silent', 'why' => 'first message']);
            $stream->close();
            continue;
        }
        $deviceKey = bin2hex((string) $handshake->remoteStaticKey());
        $counter = unpack('J', $counterBytes)[1];
        if (!isset($devices[$deviceKey]) || $counter <= $devices[$deviceKey]['counter']) {
            $note(['event' => 'silent', 'why' => isset($devices[$deviceKey]) ? 'old counter' : 'unknown device', 'counter' => $counter]);
            $stream->close();
            continue;
        }
        $devices[$deviceKey]['counter'] = $counter;
        $stream->sendFrame($handshake->writeMessage());
        $transport = $handshake->split();
        $hello = Wire::decodeHello($transport->open($stream->readFrame()));
        $note(['event' => 'connected', 'sdk' => $hello->sdk, 'counter' => $counter]);
        $kinds = array_map(static fn (string $kind): ItemKind => ItemKind::from($kind), $devices[$deviceKey]['kinds']);
        $stream->sendFrame($transport->seal(Wire::encodeReceiverMessage(
            new Welcome(1, $devices[$deviceKey]['last_seq'], $kinds, $limits),
        )));
        $photo = null;
        while (true) {
            $stream->armDeadline(30.0);
            try {
                $plaintext = $transport->open($stream->readFrame());
            } catch (Throwable) {
                $note(['event' => 'disconnected']);
                break;
            }
            $envelope = Wire::decodeRequest($plaintext);
            $seq = $envelope->seq;
            $request = $envelope->request;
            $note(['event' => 'request', 'seq' => $seq, 'json' => $plaintext]);
            $entry = $scripted();
            if ($entry !== null && ($entry['drop'] ?? false)) {
                $note(['event' => 'dropped']);
                break;
            }
            if ($entry !== null && ($entry['silent'] ?? false)) {
                sleep(60);
                break;
            }
            if ($entry !== null && isset($entry['error'])) {
                $error = $entry['error'];
                $answerSeq = match ($error['seq'] ?? 'same') {
                    'same' => $seq,
                    'none' => null,
                    default => $seq + (int) $error['seq'],
                };
                $answer = new ReceiverError(
                    $error['code'],
                    $answerSeq,
                    $error['last_seq'] ?? null,
                    $error['retry_after_ms'] ?? null,
                    $error['supported'] ?? null,
                    $error['fatal'] ?? false,
                );
                $stream->sendFrame($transport->seal(Wire::encodeReceiverMessage($answer)));
                if ($answer->fatal || $answer->code === 'paused' || $answer->code === 'replaced') {
                    $stream->finishSending();
                    usleep(100000);
                    break;
                }
                continue;
            }
            $last = $devices[$deviceKey]['last_seq'];
            if ($seq < $last) {
                $answer = ReceiverError::of(\Erdigo\Core\ErrorCode::StaleSeq, $seq, $last);
            } elseif ($seq === $last) {
                $answer = new Ack($seq, true);
            } else {
                $kind = match (true) {
                    $request instanceof PutPin => 'pin',
                    $request instanceof PutMarker => 'marker',
                    $request instanceof PutNote => 'note',
                    $request instanceof PhotoBegin, $request instanceof PhotoChunk, $request instanceof PhotoEnd => 'photo',
                    $request instanceof TrackAppend, $request instanceof TrackFinish => 'track',
                    default => null,
                };
                $devices[$deviceKey]['last_seq'] = $seq;
                if ($kind !== null && !in_array($kind, $devices[$deviceKey]['kinds'], true)) {
                    $answer = ReceiverError::of(\Erdigo\Core\ErrorCode::NotPermitted, $seq);
                } elseif ($request instanceof \Erdigo\Core\Message\Move && ($config['missing'] ?? null) === $request->id) {
                    $answer = ReceiverError::of(\Erdigo\Core\ErrorCode::NotFound, $seq);
                } else {
                    $answer = new Ack($seq, false);
                    if ($request instanceof PhotoBegin) {
                        $photo = ['header' => $request->header, 'data' => '', 'next' => 0];
                    } elseif ($request instanceof PhotoChunk && $photo !== null && $request->index === $photo['next']) {
                        $photo['data'] .= $request->data;
                        $photo['next'] += 1;
                    } elseif ($request instanceof PhotoEnd && $photo !== null) {
                        $ok = strlen($photo['data']) === $photo['header']->size
                            && hash('sha256', $photo['data']) === $photo['header']->sha256
                            && $photo['next'] === $photo['header']->chunks;
                        $note(['event' => $ok ? 'photo_stored' : 'photo_invalid', 'size' => strlen($photo['data'])]);
                        if (!$ok) {
                            $answer = ReceiverError::of(\Erdigo\Core\ErrorCode::PhotoInvalid, $seq);
                        }
                        $photo = null;
                    }
                }
            }
            $stream->sendFrame($transport->seal(Wire::encodeReceiverMessage($answer)));
        }
    } catch (WireException $error) {
        $note(['event' => 'invalid', 'code' => $error->errorCode->value, 'detail' => $error->detail]);
    } catch (Throwable $error) {
        $note(['event' => 'error', 'detail' => get_class($error) . ': ' . $error->getMessage()]);
    }
    $stream->close();
}
