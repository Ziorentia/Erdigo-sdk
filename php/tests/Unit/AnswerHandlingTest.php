<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\Coordinate;
use Erdigo\ErrorKind;
use Erdigo\RetryPolicy;
use Erdigo\Tests\Support\EndToEnd;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

/**
 * How the client takes error answers, including ones a receiver would not give by itself. The scripted receiver
 * replaces its answer to chosen requests and records the numbers it was sent.
 */
final class AnswerHandlingTest extends TestCase
{
    /** @param array<string, mixed> $error */
    private static function scripted(array $error, int $times = 1, ?RetryPolicy $retry = null): EndToEnd
    {
        return EndToEnd::start(['script' => [['error' => $error, 'times' => $times]]], retry: $retry);
    }

    // Answers that end the connection

    public function testFatalRateLimitedEndsTheConnectionWithOneError(): void
    {
        $device = self::scripted(['code' => 'rate_limited', 'retry_after_ms' => 50, 'fatal' => true]);
        $error = EndToEnd::expect($this, ErrorKind::Rejected, static fn () => $device->client->ping());
        $this->assertSame('rate_limited', $error->receiverError?->code);
        $this->assertTrue($error->receiverError?->fatal);
        // Sent once, never resent on the closed connection or on a new one.
        $this->assertSame([1], $device->receiver->seen());
        $this->assertSame(1, $device->receiver->connections());
        $this->assertNull($device->client->limits());
    }

    /** @return array<string, array{0: string, 1: ErrorKind, 2: bool}> */
    public static function fatalAnswers(): array
    {
        return [
            'rate_limited' => ['rate_limited', ErrorKind::Rejected, false],
            'rate_limited for another number' => ['rate_limited', ErrorKind::Rejected, true],
            'too_many_errors' => ['too_many_errors', ErrorKind::Rejected, false],
            'too_many_errors for another number' => ['too_many_errors', ErrorKind::Rejected, true],
            'internal_error' => ['internal_error', ErrorKind::Rejected, false],
            'replaced' => ['replaced', ErrorKind::Replaced, false],
            'replaced for another number' => ['replaced', ErrorKind::Replaced, true],
            'seq_conflict' => ['seq_conflict', ErrorKind::SequenceConflict, false],
            'stale_seq for another number' => ['stale_seq', ErrorKind::SequenceConflict, true],
            'a future code' => ['future_code', ErrorKind::Rejected, true],
        ];
    }

    /** A fatal answer is about the connection, so a number it carries for another request changes nothing. */
    #[DataProvider('fatalAnswers')]
    public function testFatalAnswersEndTheConnectionAsTheirCodeSays(string $code, ErrorKind $expected, bool $numberForAnotherRequest): void
    {
        $device = self::scripted([
            'code' => $code,
            'seq' => $numberForAnotherRequest ? 7 : 'same',
            'last_seq' => $numberForAnotherRequest ? 1000 : null,
            'retry_after_ms' => 50,
            'fatal' => true,
        ]);
        $error = EndToEnd::expect($this, $expected, static fn () => $device->client->ping());
        $this->assertSame([1], $device->receiver->seen());
        $this->assertSame(1, $device->receiver->connections());
        $this->assertNull($device->client->limits());
        if ($expected === ErrorKind::SequenceConflict) {
            // The number in an answer to another request is not taken as where the receiver stands.
            $this->assertSame(0, $error->lastSeq);
        }
    }

    public function testPausedMeansReconnectAndResendWithTheSameNumber(): void
    {
        $device = self::scripted(['code' => 'paused', 'seq' => 'none', 'fatal' => true]);
        $device->client->ping();
        $this->assertSame([1, 1], $device->receiver->seen());
        $this->assertSame(2, $device->receiver->connections());
    }

    public function testPausedWithoutRetryingReportsNoAnswer(): void
    {
        $device = self::scripted(['code' => 'paused', 'seq' => 'none', 'fatal' => true], retry: RetryPolicy::none());
        EndToEnd::expect($this, ErrorKind::NoAnswer, static fn () => $device->client->ping());
        $this->assertSame([1], $device->receiver->seen());
    }

    // Answers that keep the connection

    /** @return array<string, array{0: bool}> */
    public static function withAndWithoutNumber(): array
    {
        return ['with the number' => [true], 'without a number' => [false]];
    }

    #[DataProvider('withAndWithoutNumber')]
    public function testRateLimitedIsResentWithTheSameNumberOnTheSameConnection(bool $answerCarriesTheNumber): void
    {
        // A receiver leaves out the number when it limits a message before reading it.
        $device = self::scripted(['code' => 'rate_limited', 'seq' => $answerCarriesTheNumber ? 'same' : 'none', 'retry_after_ms' => 20], 2);
        $device->client->ping();
        $device->client->ping();
        $this->assertSame([1, 1, 1, 2], $device->receiver->seen());
        $this->assertSame(1, $device->receiver->connections());
    }

    public function testRateLimitedWaitsAsLongAsItIsTold(): void
    {
        $device = self::scripted(['code' => 'rate_limited', 'retry_after_ms' => 600]);
        $started = microtime(true);
        $device->client->ping();
        $this->assertGreaterThanOrEqual(0.55, microtime(true) - $started);
    }

    public function testAReceiverThatNeverStopsLimitingIsGivenUpOn(): void
    {
        $device = self::scripted(['code' => 'rate_limited', 'retry_after_ms' => 1], 1000);
        $error = EndToEnd::expect($this, ErrorKind::Rejected, static fn () => $device->client->ping());
        $this->assertSame('rate_limited', $error->receiverError?->code);
        // The first answer and twenty more, all for the same number.
        $this->assertSame(array_fill(0, 21, 1), $device->receiver->seen());
        $this->assertSame(1, $device->receiver->connections());
    }

    /** @return array<string, array{0: string}> */
    public static function answersForAnotherNumber(): array
    {
        return ['not_found' => ['not_found'], 'rate_limited' => ['rate_limited'], 'stale_seq' => ['stale_seq']];
    }

    #[DataProvider('answersForAnotherNumber')]
    public function testAnAnswerForAnotherNumberIsAProtocolViolation(string $code): void
    {
        $device = self::scripted(['code' => $code, 'seq' => 5, 'last_seq' => 1000, 'retry_after_ms' => 20]);
        EndToEnd::expect($this, ErrorKind::ProtocolViolation, static fn () => $device->client->ping());
        $this->assertSame([1], $device->receiver->seen());
        // The connection was dropped, and the next request uses the receiver's own count.
        $device->client->ping();
        $this->assertSame([1, 1], $device->receiver->seen());
        $this->assertSame(2, $device->receiver->connections());
    }

    public function testAFailedRequestUsesUpItsNumber(): void
    {
        $device = EndToEnd::start(['missing' => 'nowhere']);
        $error = EndToEnd::expect($this, ErrorKind::Rejected, static fn () => $device->client->move('nowhere', new Coordinate(1, 2)));
        $this->assertSame('not_found', $error->receiverError?->code);
        $device->client->ping();
        $this->assertSame([1, 2], $device->receiver->seen());
    }

    public function testStaleSeqIsASequenceConflictWithTheReceiversNumber(): void
    {
        $device = self::scripted(['code' => 'stale_seq', 'last_seq' => 40]);
        $error = EndToEnd::expect($this, ErrorKind::SequenceConflict, static fn () => $device->client->ping());
        $this->assertSame(40, $error->lastSeq);
        // The connection stays, and the next request continues after the receiver's number.
        $device->client->ping();
        $this->assertSame([1, 41], $device->receiver->seen());
        $this->assertSame(1, $device->receiver->connections());
    }
}
