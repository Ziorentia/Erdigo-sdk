<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\Coordinate;
use Erdigo\Core\ErrorCode;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\JsonObject;
use Erdigo\Core\JsonParseException;
use Erdigo\Core\JsonWriter;
use Erdigo\Core\Limits;
use Erdigo\Core\MarkerColor;
use Erdigo\Core\Message\Ack;
use Erdigo\Core\Message\Delete;
use Erdigo\Core\Message\DeleteAll;
use Erdigo\Core\Message\Move;
use Erdigo\Core\Message\PairStatus;
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
use Erdigo\Core\PairingCode;
use Erdigo\Core\PairingTicket;
use Erdigo\Core\PhotoType;
use Erdigo\Core\StrictJson;
use Erdigo\Core\TextRules;
use Erdigo\Core\TokenBucket;
use Erdigo\Core\TrackPoint;
use Erdigo\Core\Utf8;
use Erdigo\Core\WireException;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

final class ModelTest extends TestCase
{
    private const CODE = 'TEST0-0NLYC-0DE00-00000';

    private static function fingerprint(string $pair = 'ab'): string
    {
        return str_repeat($pair, 32);
    }

    private function expectWire(ErrorCode $code, callable $body): void
    {
        try {
            $body();
        } catch (WireException $error) {
            $this->assertSame($code, $error->errorCode);
            return;
        }
        $this->fail('expected ' . $code->value);
    }

    public function testPairingCodeParsing(): void
    {
        $code = new PairingCode('abcde-fghjk mnpqr-tvwxy');
        $this->assertSame('ABCDEFGHJKMNPQRTVWXY', $code->canonical);
        $this->assertSame('ABCDE-FGHJK-MNPQR-TVWXY', $code->display());
        $this->assertSame('00111000000000000000', (new PairingCode('O0IL1-00000-00000-00000'))->canonical);
        // Unicode white space is ignored, as on the other SDKs.
        $this->assertSame('ABCDEFGHJKMNPQRTVWXY', (new PairingCode("ABCDE\u{00A0}FGHJK\u{3000}MNPQR\x0CTVWXY"))->canonical);
        foreach (['ABCDE', 'UUUUU-UUUUU-UUUUU-UUUUU', 'ÄÖÜÄÖ-00000-00000-00000', "ABCDE-FGHJK-MNPQR-TVWX\xED\xA0\x80Y", "ABCDE-FGHJK-MNPQR-TVWX\xFFY", ''] as $text) {
            $this->expectWire(ErrorCode::InvalidMessage, static fn () => new PairingCode($text));
        }
    }

    public function testPairingCodeDoesNotShowItself(): void
    {
        $code = PairingCode::generate();
        $this->assertSame(20, strlen($code->canonical));
        $this->assertStringNotContainsString($code->canonical, (string) $code);
        $this->assertStringNotContainsString($code->canonical, print_r($code, true));
        $this->assertStringNotContainsString($code->canonical, print_r(new PairingTicket($code), true));
        $this->assertStringNotContainsString($code->canonical, (string) new PairingTicket($code));
    }

    public function testGeneratedCodesDiffer(): void
    {
        $codes = [];
        for ($index = 0; $index < 1000; $index++) {
            $codes[PairingCode::generate()->canonical] = true;
        }
        $this->assertCount(1000, $codes);
    }

    public function testValidQrTextWorksAndRoundTrips(): void
    {
        $code = new PairingCode(self::CODE);
        $ticket = PairingTicket::parse('erdigo-pair:1?code=' . self::CODE . '&fp=' . self::fingerprint());
        $this->assertSame(self::fingerprint(), $ticket->fingerprint);
        $this->assertTrue($ticket->code->equals($code));
        $this->assertSame('erdigo-pair:1?code=' . $code->display() . '&fp=' . self::fingerprint(), $ticket->uri());
        $this->assertSame($ticket->uri(), PairingTicket::parse((string) $ticket->uri())->uri());
        // In either order, with white space around it, and with the fingerprint in capitals.
        $this->assertSame($ticket->uri(), PairingTicket::parse(" \n erdigo-pair:1?fp=" . self::fingerprint() . '&code=' . self::CODE . "\r\n")->uri());
        $this->assertSame($ticket->uri(), PairingTicket::parse('erdigo-pair:1?code=' . self::CODE . '&fp=' . strtoupper(self::fingerprint()))->uri());
    }

    public function testABareCodeStaysTheTypedPath(): void
    {
        foreach ([self::CODE, ' ' . self::CODE . "\n", 'test00n1yc0de0000000'] as $text) {
            $ticket = PairingTicket::parse($text);
            $this->assertNull($ticket->fingerprint);
            $this->assertNull($ticket->uri());
        }
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => PairingTicket::parse(''));
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => PairingTicket::parse('TEST0'));
    }

    /** @return array<string, array{0: string}> */
    public static function malformedQrText(): array
    {
        $code = self::CODE;
        $fingerprint = self::fingerprint();
        $other = self::fingerprint('cd');
        return [
            'nothing after the prefix' => ['erdigo-pair:1?'],
            'no fingerprint' => ["erdigo-pair:1?code={$code}"],
            'no code' => ["erdigo-pair:1?fp={$fingerprint}"],
            'empty fingerprint' => ["erdigo-pair:1?code={$code}&fp="],
            'fingerprint without an equals sign' => ["erdigo-pair:1?code={$code}&fp"],
            'empty code' => ["erdigo-pair:1?code=&fp={$fingerprint}"],
            'code without an equals sign' => ["erdigo-pair:1?code&fp={$fingerprint}"],
            'fingerprint one digit short' => ["erdigo-pair:1?code={$code}&fp=" . substr($fingerprint, 0, -1)],
            'fingerprint one digit long' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}a"],
            'fingerprint of half the length' => ["erdigo-pair:1?code={$code}&fp=" . substr($fingerprint, 0, 32)],
            'fingerprint that is not hexadecimal' => ["erdigo-pair:1?code={$code}&fp=" . str_repeat('zz', 32)],
            'fingerprint with an equals sign' => ["erdigo-pair:1?code={$code}&fp=" . substr($fingerprint, 0, -1) . '='],
            'fingerprint in other digits' => ["erdigo-pair:1?code={$code}&fp=" . str_repeat("\u{FF11}", 64)],
            'fingerprint with a line break' => ["erdigo-pair:1?code={$code}&fp=" . substr($fingerprint, 0, 32) . "\n" . substr($fingerprint, 0, 31)],
            'repeated fingerprint' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}&fp={$fingerprint}"],
            'two fingerprints' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}&fp={$other}"],
            'repeated code' => ["erdigo-pair:1?code={$code}&code={$code}&fp={$fingerprint}"],
            'repeated code after the fingerprint' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}&code={$code}"],
            'an address' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}&host=192.0.2.1"],
            'an unknown field in front' => ["erdigo-pair:1?x=1&code={$code}&fp={$fingerprint}"],
            'an unknown field with no value' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}&x"],
            'a field with no name' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}&=1"],
            'an equals sign before the code' => ["erdigo-pair:1?=code={$code}&fp={$fingerprint}"],
            'an equals sign before both' => ["erdigo-pair:1?=code={$code}&=fp={$fingerprint}"],
            'several equals signs before both' => ["erdigo-pair:1?===code={$code}&===fp={$fingerprint}"],
            'nothing but an equals sign' => ['erdigo-pair:1?='],
            'an empty field in the middle' => ["erdigo-pair:1?code={$code}&&fp={$fingerprint}"],
            'an empty field in front' => ["erdigo-pair:1?&code={$code}&fp={$fingerprint}"],
            'a trailing separator' => ["erdigo-pair:1?code={$code}&fp={$fingerprint}&"],
            'only separators' => ['erdigo-pair:1?&&'],
            'field names in capitals' => ["erdigo-pair:1?CODE={$code}&FP={$fingerprint}"],
            'another version' => ["erdigo-pair:2?code={$code}&fp={$fingerprint}"],
            'no question mark' => ["erdigo-pair:1code={$code}&fp={$fingerprint}"],
            'a prefix in capitals' => ["ERDIGO-PAIR:1?code={$code}&fp={$fingerprint}"],
        ];
    }

    #[DataProvider('malformedQrText')]
    public function testQrTextNeedsExactlyOneCodeAndOneValidFingerprint(string $text): void
    {
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => PairingTicket::parse($text));
    }

    public function testPairingTextOverTheLimitIsRefused(): void
    {
        $qr = 'erdigo-pair:1?code=' . self::CODE . '&fp=' . self::fingerprint();
        $this->assertSame(110, strlen($qr));
        foreach ([$qr, self::CODE] as $text) {
            $atTheLimit = $text . str_repeat(' ', 1024 - strlen($text));
            PairingTicket::parse($atTheLimit);
            $this->expectWire(ErrorCode::InvalidMessage, static fn () => PairingTicket::parse($atTheLimit . ' '));
        }
        // The limit counts bytes, not characters.
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => PairingTicket::parse(self::CODE . str_repeat("\u{3000}", 342)));
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => PairingTicket::parse('erdigo-pair:1?' . str_repeat('&', 2000000)));
    }

    public function testOnlyAValidFingerprintBecomesQrText(): void
    {
        $code = new PairingCode(self::CODE);
        foreach (['', 'AB' . str_repeat('ab', 31), str_repeat('ab', 31), str_repeat('zz', 32)] as $text) {
            $this->assertNull((new PairingTicket($code, $text))->uri());
        }
        $this->assertNotNull((new PairingTicket($code, self::fingerprint()))->uri());
    }

    public function testCoordinatesAreValidated(): void
    {
        new Coordinate(-90, 180);
        new Coordinate(90.0, -180.0);
        foreach ([[NAN, 0], [0, INF], [90.0001, 0], [0, -180.5]] as [$latitude, $longitude]) {
            $this->expectWire(ErrorCode::InvalidCoordinate, static fn () => new Coordinate($latitude, $longitude));
        }
    }

    public function testTrackPointsAreValidated(): void
    {
        $at = new Coordinate(1, 2);
        new TrackPoint($at, 0, -1000);
        new TrackPoint($at, IntegrationV1::MAX_SEQUENCE, 100000);
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => new TrackPoint($at, -1));
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => new TrackPoint($at, IntegrationV1::MAX_SEQUENCE + 1));
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => new TrackPoint($at, null, 100000.5));
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => new TrackPoint($at, null, NAN));
    }

    public function testTextRules(): void
    {
        $this->expectWire(ErrorCode::InvalidText, static fn () => TextRules::validateTitle(''));
        $this->expectWire(ErrorCode::InvalidText, static fn () => TextRules::validateTitle("a\nb"));
        $this->expectWire(ErrorCode::InvalidText, static fn () => TextRules::validateTitle("a\u{202E}b"));
        $this->expectWire(ErrorCode::TooLarge, static fn () => TextRules::validateTitle(str_repeat('a', 201)));
        TextRules::validateTitle(str_repeat('é', 200));
        $this->expectWire(ErrorCode::InvalidText, static fn () => TextRules::validateTitle("a\xED\xA0\x80"));
        $this->expectWire(ErrorCode::InvalidText, static fn () => TextRules::validateTitle("a\xFF"));
        TextRules::validateNote("line\nline\ttab");
        $this->expectWire(ErrorCode::TooLarge, static fn () => TextRules::validateNote(str_repeat('é', 2049)));
        $this->expectWire(ErrorCode::InvalidText, static fn () => TextRules::validateNote("\u{FEFF}hello"));
        $name = TextRules::sanitizeDeviceName("  Bank\u{202E}cod.exe\x07\n" . str_repeat('x', 60) . "\xED\xA0\x80");
        $this->assertCount(40, Utf8::codePoints($name) ?? []);
        $this->assertSame('Bankcod.exe', substr($name, 0, 11));
        $this->assertSame('Zürich', TextRules::sanitizeDeviceName("\u{3000}Zürich\u{00A0}"));
    }

    /** @return array<string, array{0: Request}> */
    public static function requests(): array
    {
        $at = new Coordinate(37.5796, 126.977);
        return [
            'ping' => [new Ping()],
            'pin' => [new PutPin('gate', $at, 'Palace gate')],
            'pin without title' => [new PutPin('gate', $at, null)],
            'marker' => [new PutMarker('m1', $at, 'Tower', MarkerColor::Blue)],
            'note' => [new PutNote('n1', $at, "First line\nSecond \"line\" \\ é")],
            'move' => [new Move('gate', $at)],
            'delete' => [new Delete('gate')],
            'delete all' => [new DeleteAll()],
            'photo begin' => [new PhotoBegin(new PhotoHeader('v', $at, 'View', PhotoType::Jpeg, 70000, str_repeat('0', 64), 2))],
            'photo chunk' => [new PhotoChunk('v', 1, "\x00\x01\x02\xFF")],
            'photo end' => [new PhotoEnd('v')],
            'track append' => [new TrackAppend('walk', 'Walk', [new TrackPoint($at, 1700000000000, 38.5), new TrackPoint($at)])],
            'track finish' => [new TrackFinish('walk')],
        ];
    }

    #[DataProvider('requests')]
    public function testRequestsRoundTrip(Request $request): void
    {
        $data = Wire::encodeRequest(new RequestEnvelope(7, $request));
        $decoded = Wire::decodeRequest($data);
        $this->assertSame(7, $decoded->seq);
        $this->assertEquals($request, $decoded->request);
        $this->assertSame($data, Wire::encodeRequest($decoded));
    }

    public function testReceiverMessagesRoundTrip(): void
    {
        $messages = [
            new Welcome(1, 41, [ItemKind::Pin, ItemKind::Track], Limits::v1()),
            new Ack(3, true),
            new ReceiverError('rate_limited', 4, 2, 250, [1, 2], true),
            new ReceiverError('future_code'),
            new ReceiverError('version_unsupported', null, null, null, [], true),
            PairStatus::pending(),
            PairStatus::denied(),
            PairStatus::approved([ItemKind::Pin], 1700000000000),
        ];
        foreach ($messages as $message) {
            $data = Wire::encodeReceiverMessage($message);
            $this->assertEquals($message, Wire::decodeReceiverMessage($data), $data);
        }
    }

    /** @return array<string, array{0: string}> */
    public static function malformedReceiverMessages(): array
    {
        $limits = '"limits":{"items":500,"photos":50,"photo_bytes":1048576,"note_bytes":4096,"title_chars":200,"track_points":3000,'
            . '"open_tracks":5,"tracks":200,"msgs_per_s":5,"msg_burst":20,"points_per_s":1,"point_burst":99,"disk_bytes":31457280}';
        return [
            'extra field' => ['{"t":"ack","seq":1,"dup":false,"extra":1}'],
            'no limits' => ['{"t":"welcome","version":1,"last_seq":0,"kinds":[]}'],
            'bad code' => ['{"t":"error","code":"BAD CODE"}'],
            'approved without kinds' => ['{"t":"pair_status","status":"approved"}'],
            'approved without counter' => ['{"t":"pair_status","status":"approved","kinds":["pin"]}'],
            'unknown type' => ['{"t":"mystery"}'],
            'small point burst' => ['{"t":"welcome","version":1,"last_seq":0,"kinds":[],' . $limits . '}'],
            'ack of seq 0' => ['{"t":"ack","seq":0,"dup":false}'],
            'too many supported' => ['{"t":"error","code":"x","supported":[1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1]}'],
        ];
    }

    #[DataProvider('malformedReceiverMessages')]
    public function testSdkRejectsMalformedReceiverMessages(string $text): void
    {
        $this->expectWire(ErrorCode::InvalidMessage, static fn () => Wire::decodeReceiverMessage($text));
    }

    /** @return array<string, array{0: string}> */
    public static function strictJsonRejections(): array
    {
        $cases = [];
        foreach (['', '{', '{}{}', '{"a":1,}', '[1,]', '01', '1.', '.5', '+1', '1e', 'tru', '"\\x"', "\"\x01\"", '1e999',
            '[[[[[[[1]]]]]]]', '{"a":1,"a":1}', "\xEF\xBB\xBF{}", '"\\ud800"', '"\\udc00"', '"\\ud800\\u0041"', 'NaN', '-'] as $text) {
            $cases[json_encode($text)] = [$text];
        }
        return $cases;
    }

    #[DataProvider('strictJsonRejections')]
    public function testStrictJsonRejections(string $text): void
    {
        $this->expectException(JsonParseException::class);
        StrictJson::parse($text);
    }

    public function testStrictJsonAcceptances(): void
    {
        $this->assertSame('😀', StrictJson::parse('"😀"'));
        $this->assertSame('😀', StrictJson::parse('"\\ud83d\\ude00"'));
        $this->assertSame("a/b\"\\\x08\x0C\n\r\t", StrictJson::parse('"a\\/b\\"\\\\\\b\\f\\n\\r\\t"'));
        $this->assertSame(-1, StrictJson::parse('-1'));
        $this->assertSame(0, StrictJson::parse('-0'));
        $this->assertSame(150.0, StrictJson::parse('1.5e2'));
        $this->assertSame(PHP_INT_MAX, StrictJson::parse('9223372036854775807'));
        $this->assertSame(PHP_INT_MIN, StrictJson::parse('-9223372036854775808'));
        $this->assertSame(9223372036854775808.0, StrictJson::parse('9223372036854775808'));
        $this->assertEquals(new JsonObject(['a' => [1, true, null]]), StrictJson::parse(" {\"a\" : [1 , true,null] }\n"));
        $this->assertEquals([[[[[[1]]]]]], StrictJson::parse('[[[[[[1]]]]]]'));
    }

    public function testMessageSizeLimit(): void
    {
        $tooLong = '{"t":"ping","seq":1,"x":"' . str_repeat('a', 65519) . '"}';
        $this->expectWire(ErrorCode::TooLarge, static fn () => Wire::decodeRequest($tooLong));
    }

    public function testTokenBucket(): void
    {
        $bucket = new TokenBucket(2, 1, 100.0);
        $this->assertNull($bucket->take(100.0));
        $this->assertNull($bucket->take(100.0));
        $this->assertEqualsWithin(1.0, $bucket->take(100.0));
        $this->assertNull($bucket->take(101.0));
        $small = new TokenBucket(2, 1, 0.0);
        $this->assertNotNull($small->take(0.0, 5));
    }

    private function assertEqualsWithin(float $expected, ?float $actual): void
    {
        $this->assertNotNull($actual);
        $this->assertLessThan(0.001, abs($expected - (float) $actual));
    }

    /** @return array<string, array{0: float, 1: string}> */
    public static function numbers(): array
    {
        $cases = [];
        foreach ([
            [0.0, '0.0'], [-0.0, '-0.0'], [1.0, '1.0'], [37.0, '37.0'], [37.5796, '37.5796'], [126.977, '126.977'],
            [-33.8568, '-33.8568'], [0.0001, '0.0001'], [0.00001, '1e-05'], [0.000123, '0.000123'], [1e-7, '1e-07'],
            [1e15, '1000000000000000.0'], [1e16, '1e+16'], [9007199254740992.0, '9007199254740992.0'],
            [18014398509481984.0, '1.8014398509481984e+16'], [1.5e16, '1.5e+16'], [123456.789, '123456.789'],
            [100000.0, '100000.0'], [-1000.0, '-1000.0'], [38.5, '38.5'], [5e-324, '5e-324'], [1e-300, '1e-300'],
            [0.1, '0.1'], [0.30000000000000004, '0.30000000000000004'], [1e21, '1e+21'],
            [12345678901234567890.0, '1.2345678901234567e+19'],
        ] as [$value, $text]) {
            $cases[$text] = [$value, $text];
        }
        return $cases;
    }

    #[DataProvider('numbers')]
    public function testNumbersAreWrittenLikeTheOtherSdks(float $value, string $expected): void
    {
        $this->assertSame($expected, JsonWriter::formatNumber($value));
        $this->assertSame($value, (float) $expected);
        // Whatever the ini setting says.
        $previous = ini_get('serialize_precision');
        ini_set('serialize_precision', '17');
        try {
            $this->assertSame($expected, JsonWriter::formatNumber($value));
        } finally {
            ini_set('serialize_precision', (string) $previous);
        }
    }

    public function testJsonTextIsWrittenLikeTheOtherSdks(): void
    {
        // Only quotes, backslashes and control characters are escaped, as the other SDKs do.
        $this->assertSame('{"a":"x/y \"q\" \\\\ \n\t\r\u0001\u001f é ' . "\u{2028}" . ' €","b":[1,2.5,true,null]}', JsonWriter::encode([
            'a' => "x/y \"q\" \\ \n\t\r\x01\x1F é \u{2028} €",
            'b' => [1, 2.5, true, null],
        ]));
    }

    public function testFingerprintsAndShortForms(): void
    {
        $fingerprint = IntegrationV1::fingerprint(str_repeat("\x01", 32));
        $this->assertSame(64, strlen($fingerprint));
        $this->assertSame(implode('-', str_split(substr($fingerprint, 0, 16), 4)), IntegrationV1::shortFingerprint($fingerprint));
        $this->assertSame(IntegrationV1::MODE_CONNECT, IntegrationV1::parsePreamble(IntegrationV1::preamble(IntegrationV1::MODE_CONNECT)));
        $this->assertSame(IntegrationV1::MODE_PAIR, IntegrationV1::parsePreamble("EIN1\x02"));
        $this->assertNull(IntegrationV1::parsePreamble("EIN1\x03"));
        $this->assertNull(IntegrationV1::parsePreamble("EIN2\x01"));
    }

    public function testPhotoTypesAreDetectedFromTheirFirstBytes(): void
    {
        $this->assertSame(PhotoType::Jpeg, PhotoType::detect("\xFF\xD8\xFF\xE0"));
        $this->assertSame(PhotoType::Png, PhotoType::detect("\x89PNG\r\n\x1A\nrest"));
        $this->assertNull(PhotoType::detect("\xFF\xD8"));
        $this->assertNull(PhotoType::detect('GIF89a'));
        $this->assertNull(PhotoType::detect(''));
    }
}
