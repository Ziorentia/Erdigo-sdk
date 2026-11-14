<?php

declare(strict_types=1);

namespace Erdigo;

use Erdigo\Core\Clock;
use Erdigo\Core\Coordinate;
use Erdigo\Core\ErrorCode;
use Erdigo\Core\FrameStream;
use Erdigo\Core\FrameStreamException;
use Erdigo\Core\Hex;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemId;
use Erdigo\Core\ItemKind;
use Erdigo\Core\Limits;
use Erdigo\Core\MarkerColor;
use Erdigo\Core\Message\Ack;
use Erdigo\Core\Message\Delete;
use Erdigo\Core\Message\DeleteAll;
use Erdigo\Core\Message\Hello;
use Erdigo\Core\Message\Move;
use Erdigo\Core\Message\PhotoBegin;
use Erdigo\Core\Message\PhotoChunk;
use Erdigo\Core\Message\PhotoEnd;
use Erdigo\Core\Message\PhotoHeader;
use Erdigo\Core\Message\Ping;
use Erdigo\Core\Message\PutMarker;
use Erdigo\Core\Message\PutNote;
use Erdigo\Core\Message\PutPin;
use Erdigo\Core\Message\ReceiverError;
use Erdigo\Core\Message\Request;
use Erdigo\Core\Message\RequestEnvelope;
use Erdigo\Core\Message\TrackAppend;
use Erdigo\Core\Message\TrackFinish;
use Erdigo\Core\Message\Welcome;
use Erdigo\Core\Message\Wire;
use Erdigo\Core\Noise\HandshakeState;
use Erdigo\Core\Noise\NoiseException;
use Erdigo\Core\Noise\Pattern;
use Erdigo\Core\Noise\Transport;
use Erdigo\Core\PhotoType;
use Erdigo\Core\TextRules;
use Erdigo\Core\TokenBucket;
use Erdigo\Core\TrackPoint;
use Erdigo\Core\WireException;

/**
 * Writes items to one paired receiver.
 *
 * Each request returns once the receiver has acknowledged it. If the connection breaks, the request is sent again with
 * the same sequence number, so the receiver applies it at most once. The connection stays open for the next request
 * until {@see self::close()} is called or the client goes away.
 *
 * With a {@see FileDeviceStore}, the client holds a lock on this receiver's connection while its connection is open, so
 * that other processes sharing the store, such as the workers of a web server, wait their turn instead of replacing
 * each other's connection. A program that keeps its connection open keeps the others waiting, so close it after use.
 */
final class ErdigoClient
{
    /**
     * How many `rate_limited` answers one request will accept before giving up. An honest receiver needs only one or
     * two, so this only stops a receiver that never relents.
     */
    private const MAX_RATE_LIMITED_ANSWERS = 20;
    private const MINIMUM_RATE_LIMIT_WAIT = 0.05;

    private ReceiverPairing $pairing;
    private readonly ReceiverTarget $target;
    private readonly RetryPolicy $retry;
    private ?FrameStream $stream = null;
    private ?Transport $transport = null;
    private ?Welcome $welcome = null;
    private int $lastSeq = 0;
    private ?ConnectionLock $lock = null;
    private float $lastSessionAt = 0.0;
    /** When the request in progress must end, or null. */
    private ?float $deadline = null;

    // These outlive a connection, because the receiver's own count does too.
    private ?Limits $pacedLimits = null;
    private ?TokenBucket $messageBucket = null;
    private ?TokenBucket $pointBucket = null;

    /**
     * @param DeviceIdentity $identity This device's key.
     * @param ReceiverPairing $pairing The receiver to write to.
     * @param DeviceStore $store Where the connection counter and the receiver's last address are kept.
     * @param ReceiverTarget|null $target Where to look for the receiver. By default the last known address, then the
     *     local network.
     * @param RetryPolicy|null $retry How long each outage may last before a request fails with
     *     {@see ErrorKind::NoAnswer}. By default 30 seconds. This does not limit a request's total time.
     * @param float $connectTimeout How long one attempt to connect may take, in seconds.
     * @param float $ackTimeout How long to wait for the answer to one message, in seconds.
     * @param float|null $requestTimeout The longest one call may take in all, in seconds, after which it fails with
     *     {@see ErrorKind::TimedOut}. Useful in a web request, which has its own time limit. By default none.
     * @param bool $holdConnectionLock Whether to take the store's connection lock while connected. Leave it on.
     */
    public function __construct(
        private readonly DeviceIdentity $identity,
        ReceiverPairing $pairing,
        private readonly DeviceStore $store,
        ?ReceiverTarget $target = null,
        ?RetryPolicy $retry = null,
        private readonly float $connectTimeout = 5.0,
        private readonly float $ackTimeout = 15.0,
        private readonly ?float $requestTimeout = null,
        private readonly bool $holdConnectionLock = true,
    ) {
        $this->pairing = $pairing;
        $this->target = $target ?? ReceiverTarget::discover();
        $this->retry = $retry ?? RetryPolicy::upTo(30.0);
    }

    public function __destruct()
    {
        $this->close();
    }

    // Public requests

    /** Connects now rather than at the first request. Throws {@see ErrorKind::NoAnswer} if the retry policy runs out. */
    public function connect(): void
    {
        $this->call(fn () => $this->retrying(fn () => $this->currentSession()));
    }

    /** Closes the connection, and lets other processes using this device key connect. A later request connects again. */
    public function close(): void
    {
        $this->stream?->close();
        $this->transport?->close();
        $this->stream = null;
        $this->transport = null;
        $this->welcome = null;
        $this->lock?->release();
        $this->lock = null;
    }

    /** The limits the receiver announced on the current connection. */
    public function limits(): ?Limits
    {
        return $this->welcome?->limits;
    }

    /**
     * The item kinds the person allowed, as announced on the current connection.
     *
     * @return list<ItemKind>|null
     */
    public function allowedKinds(): ?array
    {
        return $this->welcome?->kinds;
    }

    public function ping(): void
    {
        $this->call(fn () => $this->perform(new Ping(), null, 0));
    }

    /** Creates the pin, or replaces it if this device already has one with this id. */
    public function putPin(string $id, Coordinate $at, ?string $title = null): void
    {
        $id = self::validatedId($id);
        self::validateTitle($title);
        $this->call(fn () => $this->perform(new PutPin($id, $at, $title), ItemKind::Pin, 0));
    }

    public function putMarker(string $id, Coordinate $at, ?string $title = null, ?MarkerColor $color = null): void
    {
        $id = self::validatedId($id);
        self::validateTitle($title);
        $this->call(fn () => $this->perform(new PutMarker($id, $at, $title, $color), ItemKind::Marker, 0));
    }

    /** A note is plain text. The receiver shows links as text and never opens them. */
    public function putNote(string $id, Coordinate $at, string $text): void
    {
        $id = self::validatedId($id);
        self::validated(static fn () => TextRules::validateNote($text));
        $this->call(fn () => $this->perform(new PutNote($id, $at, $text), ItemKind::Note, 0));
    }

    public function move(string $id, Coordinate $to): void
    {
        $id = self::validatedId($id);
        $this->call(fn () => $this->perform(new Move($id, $to), null, 0));
    }

    /** Deletes one of this device's items. Deleting an id that does not exist succeeds. */
    public function delete(string $id): void
    {
        $id = self::validatedId($id);
        $this->call(fn () => $this->perform(new Delete($id), null, 0));
    }

    /** Deletes every item this device created on this receiver, and nothing else. */
    public function deleteAll(): void
    {
        $this->call(fn () => $this->perform(new DeleteAll(), null, 0));
    }

    /**
     * Appends points to a track, creating it if needed. Long lists are split into several messages.
     *
     * @param list<TrackPoint> $points
     */
    public function appendTrack(string $id, array $points, ?string $title = null): void
    {
        $id = self::validatedId($id);
        self::validateTitle($title);
        if ($points === []) {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'a track append needs at least one point');
        }
        foreach ($points as $point) {
            if (!$point instanceof TrackPoint) {
                throw new ErdigoException(ErrorKind::InvalidArgument, 'track points are TrackPoint values');
            }
        }
        $all = array_values($points);
        $this->call(function () use ($id, $all, $title): void {
            $pendingTitle = $title;
            foreach (array_chunk($all, IntegrationV1::MAX_POINTS_PER_MESSAGE) as $batch) {
                $this->perform(new TrackAppend($id, $pendingTitle, $batch), ItemKind::Track, count($batch));
                $pendingTitle = null;
            }
        });
    }

    public function finishTrack(string $id): void
    {
        $id = self::validatedId($id);
        $this->call(fn () => $this->perform(new TrackFinish($id), ItemKind::Track, 0));
    }

    /**
     * Sends a JPEG or PNG photo. The format is detected from the data, and the receiver removes metadata such as EXIF
     * location when it stores the photo.
     */
    public function putPhoto(string $id, Coordinate $at, string $data, ?string $title = null): void
    {
        $id = self::validatedId($id);
        self::validateTitle($title);
        $type = PhotoType::detect($data)
            ?? throw new ErdigoException(ErrorKind::InvalidArgument, 'a photo must be JPEG or PNG data');
        $maxBytes = Limits::v1()->photoBytes;
        if (strlen($data) > $maxBytes) {
            throw new ErdigoException(ErrorKind::InvalidArgument, "a photo is at most {$maxBytes} bytes");
        }
        $chunkSize = IntegrationV1::MAX_CHUNK_BYTES;
        $chunks = str_split($data, $chunkSize);
        $header = new PhotoHeader($id, $at, $title, $type, strlen($data), Hex::encode(hash('sha256', $data, true)), count($chunks));
        $this->call(fn () => $this->retrying(function () use ($header, $chunks, $id): void {
            // The receiver drops a partial upload when the connection ends, so restart from the beginning.
            $this->currentSession();
            if (!in_array(ItemKind::Photo, $this->welcome?->kinds ?? [], true)) {
                throw new ErdigoException(ErrorKind::NotPermitted, itemKind: ItemKind::Photo);
            }
            $this->exchange(new PhotoBegin($header), $this->lastSeq + 1);
            foreach ($chunks as $index => $chunk) {
                $this->exchange(new PhotoChunk($id, $index, $chunk), $this->lastSeq + 1);
            }
            $this->exchange(new PhotoEnd($id), $this->lastSeq + 1);
        }));
    }

    // Validation

    private static function validatedId(string $id): string
    {
        return self::validated(static fn (): string => ItemId::validate($id));
    }

    private static function validateTitle(?string $title): void
    {
        if ($title !== null) {
            self::validated(static fn () => TextRules::validateTitle($title));
        }
    }

    /**
     * @template T
     * @param callable(): T $body
     * @return T
     */
    private static function validated(callable $body): mixed
    {
        try {
            return $body();
        } catch (WireException $error) {
            throw new ErdigoException(ErrorKind::InvalidArgument, $error->detail, previous: $error);
        }
    }

    // Requests

    /** Runs one public call within the request timeout, if there is one. */
    private function call(callable $body): void
    {
        $this->deadline = $this->requestTimeout === null ? null : Clock::now() + $this->requestTimeout;
        try {
            $body();
        } finally {
            $this->deadline = null;
        }
    }

    /** Fails the call once its time is up. The connection is closed, because a request may be half done. */
    private function checkDeadline(): void
    {
        if ($this->deadline !== null && Clock::now() >= $this->deadline) {
            $this->close();
            throw new ErdigoException(ErrorKind::TimedOut);
        }
    }

    /** `$seconds`, or less if the call's time runs out first. */
    private function timeLeft(float $seconds): float
    {
        if ($this->deadline === null) {
            return $seconds;
        }
        return max(0.0, min($seconds, $this->deadline - Clock::now()));
    }

    private function perform(Request $request, ?ItemKind $kind, int $points): void
    {
        $assignedSeq = null;
        // A resend is the same request, so it does not wait for its allowance a second time.
        $pointsArePaid = false;
        $this->retrying(function () use ($request, $kind, $points, &$assignedSeq, &$pointsArePaid): void {
            $this->currentSession();
            if ($kind !== null && !in_array($kind, $this->welcome?->kinds ?? [], true)) {
                throw new ErdigoException(ErrorKind::NotPermitted, itemKind: $kind);
            }
            // A request keeps its number across reconnections, so the receiver can recognise a resend.
            if ($assignedSeq !== null && $this->lastSeq > $assignedSeq) {
                throw new ErdigoException(ErrorKind::SequenceConflict, lastSeq: $this->lastSeq);
            }
            if (!$pointsArePaid) {
                $this->pace($this->pointBucket, $points);
                $pointsArePaid = true;
            }
            $seq = $assignedSeq ?? $this->lastSeq + 1;
            $assignedSeq = $seq;
            $this->exchange($request, $seq);
        });
    }

    /** Waits as long as asked, up to the longest wait a receiver may ask for, or until the call's time is up. */
    private function sleep(float $seconds): void
    {
        $seconds = max(0.0, min($seconds, 3600.0));
        $allowed = $this->timeLeft($seconds);
        if ($allowed > 0) {
            usleep((int) ($allowed * 1e6));
        }
        if ($allowed < $seconds) {
            $this->checkDeadline();
        }
    }

    private function pace(?TokenBucket $bucket, int $count): void
    {
        if ($bucket === null || $count <= 0) {
            return;
        }
        while (($wait = $bucket->take(Clock::now(), $count)) !== null) {
            $this->sleep($wait);
        }
    }

    /** Sends one request and waits for its answer, honouring the receiver's rate limit. */
    private function exchange(Request $request, int $seq): void
    {
        if ($seq > IntegrationV1::MAX_SEQUENCE) {
            throw new ErdigoException(
                ErrorKind::InvalidArgument,
                'the sequence number is exhausted; remove this device on the receiver and pair it again',
            );
        }
        $plaintext = Wire::encodeRequest(new RequestEnvelope($seq, $request));
        $rateLimitedAnswers = 0;
        while (true) {
            $this->pace($this->messageBucket, 1);
            $stream = $this->stream;
            $transport = $this->transport;
            if ($stream === null || $transport === null) {
                throw new ConnectionLostException();
            }
            try {
                $sealed = $transport->seal($plaintext);
                $stream->armDeadline($this->timeLeft($this->ackTimeout));
                $stream->sendFrame($sealed);
                $frame = $stream->readFrame();
                $stream->disarmDeadline();
                $answer = Wire::decodeReceiverMessage($transport->open($frame));
            } catch (WireException $error) {
                $this->close();
                throw new ErdigoException(ErrorKind::ProtocolViolation, $error->detail, previous: $error);
            } catch (FrameStreamException | NoiseException) {
                // A message that fails authentication ends the connection, as a broken one does.
                $this->close();
                $this->checkDeadline();
                throw new ConnectionLostException();
            }

            if ($answer instanceof Ack) {
                if ($answer->seq !== $seq) {
                    $this->close();
                    throw new ErdigoException(ErrorKind::ProtocolViolation, 'the acknowledgement has the wrong sequence number');
                }
                $this->lastSeq = $seq;
                return;
            }
            if (!$answer instanceof ReceiverError) {
                $this->close();
                throw new ErdigoException(ErrorKind::ProtocolViolation, 'unexpected message in answer to a request');
            }
            $endsConnection = self::endsConnection($answer);
            // An answer about one request must be about this one. An answer that ends the connection is about the
            // connection, so it ends it whatever number it carries.
            $answersThisRequest = $answer->seq === null || $answer->seq === $seq;
            if (!$endsConnection && !$answersThisRequest) {
                $this->close();
                throw new ErdigoException(ErrorKind::ProtocolViolation, 'the error answers a different sequence number');
            }
            // Only an answer the receiver will keep the connection open after is worth waiting for.
            if (!$endsConnection && $answer->is(ErrorCode::RateLimited)) {
                $rateLimitedAnswers += 1;
                if ($rateLimitedAnswers > self::MAX_RATE_LIMITED_ANSWERS) {
                    $this->close();
                    throw new ErdigoException(ErrorKind::Rejected, receiverError: $answer);
                }
                $wait = ($answer->retryAfterMs ?? 1000) / 1000.0;
                $this->sleep(max($wait, self::MINIMUM_RATE_LIMIT_WAIT));
                continue;
            }
            $this->handle($answer, $seq, $answersThisRequest, $endsConnection);
        }
    }

    /**
     * Whether the receiver closes the connection after this answer. `paused` and `replaced` always do, even if a
     * receiver leaves out the flag.
     */
    private static function endsConnection(ReceiverError $error): bool
    {
        return $error->fatal || $error->is(ErrorCode::Paused) || $error->is(ErrorCode::Replaced);
    }

    /** Turns an error answer into the error the caller sees. It always throws. */
    private function handle(ReceiverError $error, int $seq, bool $answersThisRequest, bool $endsConnection): never
    {
        // A number from an answer to some other request says nothing about where this connection stands.
        if ($answersThisRequest && $error->lastSeq !== null) {
            $this->lastSeq = max($this->lastSeq, $error->lastSeq);
        }
        if ($endsConnection) {
            $this->close();
        }
        if ($error->is(ErrorCode::Paused)) {
            throw new ConnectionLostException();
        }
        if ($error->is(ErrorCode::Replaced)) {
            throw new ErdigoException(ErrorKind::Replaced, receiverError: $error);
        }
        if ($error->is(ErrorCode::StaleSeq) || $error->is(ErrorCode::SeqConflict)) {
            throw new ErdigoException(ErrorKind::SequenceConflict, receiverError: $error, lastSeq: $this->lastSeq);
        }
        $code = ErrorCode::tryFrom($error->code);
        if (!$endsConnection && $error->seq === $seq && $code !== null && !$code->isRetryable()) {
            // The receiver used up this number on the failed request.
            $this->lastSeq = $seq;
        }
        throw new ErdigoException(ErrorKind::Rejected, receiverError: $error);
    }

    // Connection

    private function currentSession(): void
    {
        if ($this->transport !== null) {
            return;
        }
        if ($this->holdConnectionLock && $this->store instanceof LockingDeviceStore && $this->lock === null) {
            // Another process using this device key finishes its requests first.
            $this->lock = $this->store->acquireConnectionLock($this->pairing->fingerprint(), $this->timeLeft($this->ackTimeout));
            if ($this->lock === null) {
                $this->checkDeadline();
                throw new ConnectionLostException();
            }
        }
        try {
            // The remembered address comes first, because looking around the network takes a moment.
            if ($this->firstAnswer($this->target->knownEndpoints($this->pairing))) {
                return;
            }
            if ($this->target->isDiscover() && $this->firstAnswer(Discovery::browse())) {
                return;
            }
        } catch (ErdigoException $error) {
            // No connection came of it, so other processes need not wait.
            $this->close();
            throw $error;
        }
        $this->close();
        $this->checkDeadline();
        throw new ConnectionLostException();
    }

    /**
     * Tries each endpoint in turn. A receiver that stays silent is simply not the right one.
     *
     * @param list<array{0: string, 1: int}> $endpoints
     */
    private function firstAnswer(array $endpoints): bool
    {
        foreach ($endpoints as [$host, $port]) {
            try {
                $this->handshake($host, $port);
            } catch (ConnectionLostException) {
                $this->checkDeadline();
                continue;
            }
            $this->lastSessionAt = Clock::now();
            $this->preparePacing();
            $this->rememberAddress();
            return true;
        }
        return false;
    }

    private function preparePacing(): void
    {
        $limits = $this->welcome?->limits;
        if ($limits === null || ($this->pacedLimits !== null && $this->pacedLimits->equals($limits))) {
            return;
        }
        $this->pacedLimits = $limits;
        $now = Clock::now();
        $this->messageBucket = new TokenBucket($limits->messageBurst, $limits->messagesPerSecond, $now);
        $this->pointBucket = new TokenBucket($limits->pointBurst, $limits->pointsPerSecond, $now);
    }

    private function rememberAddress(): void
    {
        $address = $this->stream?->remoteAddress();
        if ($address === null || ($this->pairing->lastHost === $address[0] && $this->pairing->lastPort === $address[1])) {
            return;
        }
        $this->pairing = $this->pairing->withAddress($address[0], $address[1]);
        $this->store->updateAddress($this->pairing->fingerprint(), $address[0], $address[1]);
    }

    private function handshake(string $host, int $port): void
    {
        $stream = null;
        $handshake = null;
        $transport = null;
        try {
            $stream = FrameStream::connect($host, $port, max(0.001, $this->timeLeft($this->connectTimeout)));
            $prologue = IntegrationV1::preamble(IntegrationV1::MODE_CONNECT);
            $handshake = HandshakeState::start(Pattern::ik(), true, $prologue, $this->identity->privateKey(), $this->pairing->receiverKey);
            // Written to disk first: the receiver ignores a counter it has already seen.
            $counter = $this->store->nextConnectCounter($this->pairing->fingerprint(), (int) floor(microtime(true) * 1000));
            if ($counter > IntegrationV1::MAX_SEQUENCE) {
                throw new ErdigoException(
                    ErrorKind::Storage,
                    'this device key has used up its connection counter. Create a new key and pair again.',
                );
            }
            $first = $handshake->writeMessage(pack('J', $counter));
            $stream->armDeadline($this->timeLeft($this->connectTimeout));
            $stream->send($prologue . FrameStream::frame($first));
            $second = $stream->readFrame(IntegrationV1::MAX_HANDSHAKE_FRAME_LENGTH);
            if ($handshake->readMessage($second) !== '') {
                throw new ErdigoException(ErrorKind::ProtocolViolation, 'the handshake reply has an unexpected payload');
            }
            $transport = $handshake->split();
            $stream->sendFrame($transport->seal(Wire::encodeHello(new Hello([IntegrationV1::VERSION], Pairing::SDK_NAME))));
            $answer = Wire::decodeReceiverMessage($transport->open($stream->readFrame()));
            $stream->disarmDeadline();
            if ($answer instanceof Welcome) {
                if ($answer->version !== IntegrationV1::VERSION) {
                    throw new ErdigoException(ErrorKind::ProtocolViolation, 'the receiver chose a version that was not offered');
                }
                $this->stream = $stream;
                $this->transport = $transport;
                $this->welcome = $answer;
                $this->lastSeq = $answer->lastSeq;
                return;
            }
            if ($answer instanceof ReceiverError) {
                if ($answer->is(ErrorCode::VersionUnsupported)) {
                    throw new ErdigoException(ErrorKind::VersionUnsupported, receiverError: $answer, supported: $answer->supported ?? []);
                }
                if ($answer->is(ErrorCode::Paused)) {
                    throw new ConnectionLostException();
                }
                throw new ErdigoException(ErrorKind::Rejected, receiverError: $answer);
            }
            throw new ErdigoException(ErrorKind::ProtocolViolation, 'the receiver did not send welcome');
        } catch (\Throwable $error) {
            $stream?->close();
            $transport?->close();
            if ($error instanceof ErdigoException || $error instanceof ConnectionLostException) {
                throw $error;
            }
            if ($error instanceof WireException) {
                throw new ErdigoException(ErrorKind::ProtocolViolation, $error->detail, previous: $error);
            }
            if ($error instanceof FrameStreamException || $error instanceof NoiseException) {
                throw new ConnectionLostException();
            }
            throw $error;
        } finally {
            $handshake?->clear();
        }
    }

    /**
     * Runs `$body` again after connection problems until the retry policy runs out. The time allowed counts from the
     * first failure, so a long paced wait does not use it up.
     */
    private function retrying(callable $body): void
    {
        $outageStart = null;
        $attempt = 0;
        while (true) {
            try {
                $body();
                return;
            } catch (ConnectionLostException) {
                $now = Clock::now();
                // Having connected since then, this is a new outage.
                if ($outageStart !== null && $this->lastSessionAt > $outageStart) {
                    $outageStart = null;
                    $attempt = 0;
                }
                $start = $outageStart ?? $now;
                $outageStart = $start;
                $delay = self::backoff($attempt);
                $attempt += 1;
                if (!$this->retry->retries) {
                    throw new ErdigoException(ErrorKind::NoAnswer);
                }
                if ($this->retry->seconds !== null) {
                    // Use all the time allowed, with a last try at the end of it.
                    $remaining = $this->retry->seconds - ($now - $start);
                    if ($remaining <= 0) {
                        throw new ErdigoException(ErrorKind::NoAnswer);
                    }
                    $delay = min($delay, $remaining);
                }
                $this->sleep($delay);
            }
        }
    }

    /** 1, 2, 4 ... up to 60 seconds, with 20 percent jitter so several devices do not retry in step. */
    public static function backoff(int $attempt): float
    {
        $base = min(60.0, 2.0 ** min($attempt, 6));
        $jitter = 0.8 + 0.4 * (random_int(0, 1000000) / 1000000.0);
        return $base * $jitter;
    }
}
