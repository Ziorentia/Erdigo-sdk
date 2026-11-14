<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\ErrorCode;
use Erdigo\Core\Hex;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\JsonObject;
use Erdigo\Core\JsonParseException;
use Erdigo\Core\JsonWriter;
use Erdigo\Core\Limits;
use Erdigo\Core\MarkerColor;
use Erdigo\Core\PhotoType;
use Erdigo\Core\StrictJson;
use Erdigo\Core\TextRules;
use Erdigo\Core\TrackPoint;
use Erdigo\Core\WireException;

/**
 * Encodes and decodes every integration-v1 message. Decoding is an allow-list: unknown types and unknown fields are
 * rejected, and nothing about identity is ever taken from a message.
 */
final class Wire
{
    private function __construct()
    {
    }

    // Device to receiver

    public static function encodeHello(Hello $hello): string
    {
        return JsonWriter::encode(['t' => 'hello', 'versions' => $hello->versions, 'sdk' => $hello->sdk]);
    }

    public static function decodeHello(string $data): Hello
    {
        $fields = self::parse($data);
        if ($fields->string('t') !== 'hello') {
            throw new WireException(ErrorCode::InvalidMessage, 'the first message must be hello');
        }
        $fields->only('t', 'versions', 'sdk');
        $versions = $fields->versions();
        return new Hello($versions, $fields->sdkName());
    }

    public static function encodePairRequest(PairRequest $request): string
    {
        return JsonWriter::encode([
            't' => 'pair_request',
            'versions' => $request->versions,
            'sdk' => $request->sdk,
            'name' => $request->name,
            'kinds' => array_map(static fn (ItemKind $kind): string => $kind->value, $request->kinds),
        ]);
    }

    public static function decodePairRequest(string $data): PairRequest
    {
        $fields = self::parse($data);
        if ($fields->string('t') !== 'pair_request') {
            throw new WireException(ErrorCode::InvalidMessage, 'expected pair_request');
        }
        $fields->only('t', 'versions', 'sdk', 'name', 'kinds');
        $name = $fields->string('name');
        if (strlen($name) > 256) {
            throw new WireException(ErrorCode::TooLarge, 'a device name is too long');
        }
        $kinds = $fields->kinds();
        if ($kinds === []) {
            throw new WireException(ErrorCode::InvalidMessage, "field 'kinds' cannot be empty");
        }
        $versions = $fields->versions();
        return new PairRequest($versions, $fields->sdkName(), $name, $kinds);
    }

    public static function encodeRequest(RequestEnvelope $envelope): string
    {
        $request = $envelope->request;
        $fields = [];
        $put = static function (string $type) use (&$fields, $envelope): void {
            $fields['t'] = $type;
            $fields['seq'] = $envelope->seq;
        };
        $coordinate = static function (\Erdigo\Core\Coordinate $at) use (&$fields): void {
            $fields['lat'] = $at->latitude;
            $fields['lon'] = $at->longitude;
        };

        if ($request instanceof Ping) {
            $put('ping');
        } elseif ($request instanceof PutPin) {
            $put('put');
            $fields['kind'] = 'pin';
            $fields['id'] = $request->id;
            $coordinate($request->at);
            if ($request->title !== null) {
                $fields['title'] = $request->title;
            }
        } elseif ($request instanceof PutMarker) {
            $put('put');
            $fields['kind'] = 'marker';
            $fields['id'] = $request->id;
            $coordinate($request->at);
            if ($request->title !== null) {
                $fields['title'] = $request->title;
            }
            if ($request->color !== null) {
                $fields['color'] = $request->color->value;
            }
        } elseif ($request instanceof PutNote) {
            $put('put');
            $fields['kind'] = 'note';
            $fields['id'] = $request->id;
            $coordinate($request->at);
            $fields['text'] = $request->text;
        } elseif ($request instanceof Move) {
            $put('move');
            $fields['id'] = $request->id;
            $coordinate($request->to);
        } elseif ($request instanceof Delete) {
            $put('delete');
            $fields['id'] = $request->id;
        } elseif ($request instanceof DeleteAll) {
            $put('delete_all');
        } elseif ($request instanceof PhotoBegin) {
            $header = $request->header;
            $put('photo_begin');
            $fields['id'] = $header->id;
            $coordinate($header->at);
            if ($header->title !== null) {
                $fields['title'] = $header->title;
            }
            $fields['type'] = $header->type->value;
            $fields['size'] = $header->size;
            $fields['sha256'] = $header->sha256;
            $fields['chunks'] = $header->chunks;
        } elseif ($request instanceof PhotoChunk) {
            $put('photo_chunk');
            $fields['id'] = $request->id;
            $fields['index'] = $request->index;
            $fields['data'] = base64_encode($request->data);
        } elseif ($request instanceof PhotoEnd) {
            $put('photo_end');
            $fields['id'] = $request->id;
        } elseif ($request instanceof TrackAppend) {
            $put('track_append');
            $fields['id'] = $request->id;
            if ($request->title !== null) {
                $fields['title'] = $request->title;
            }
            $fields['points'] = array_map([self::class, 'pointFields'], $request->points);
        } elseif ($request instanceof TrackFinish) {
            $put('track_finish');
            $fields['id'] = $request->id;
        } else {
            throw new \InvalidArgumentException('unknown request');
        }
        return JsonWriter::encode($fields);
    }

    /** @return array<string, int|float> */
    private static function pointFields(TrackPoint $point): array
    {
        $fields = ['lat' => $point->coordinate->latitude, 'lon' => $point->coordinate->longitude];
        if ($point->timestampMs !== null) {
            $fields['ts'] = $point->timestampMs;
        }
        if ($point->altitudeM !== null) {
            $fields['alt'] = $point->altitudeM;
        }
        return $fields;
    }

    /**
     * Decodes a request. Throws {@see WireException} for any message that is not exactly what the specification
     * allows. A receiver that announced lower limits passes them here, so that it enforces what it announced.
     */
    public static function decodeRequest(string $data, ?Limits $limits = null): RequestEnvelope
    {
        $limits ??= Limits::v1();
        $fields = self::parse($data);
        $type = $fields->string('t');
        $seq = $fields->seq();

        switch ($type) {
            case 'ping':
                $fields->only('t', 'seq');
                $request = new Ping();
                break;
            case 'put':
                $kind = $fields->string('kind');
                switch ($kind) {
                    case 'pin':
                        $fields->only('t', 'seq', 'kind', 'id', 'lat', 'lon', 'title');
                        $id = $fields->id();
                        $at = $fields->coordinate();
                        $request = new PutPin($id, $at, $fields->title($limits));
                        break;
                    case 'marker':
                        $fields->only('t', 'seq', 'kind', 'id', 'lat', 'lon', 'title', 'color');
                        $color = null;
                        $text = $fields->optionalString('color');
                        if ($text !== null) {
                            $color = MarkerColor::tryFrom($text)
                                ?? throw new WireException(ErrorCode::InvalidMessage, 'unknown marker color');
                        }
                        $id = $fields->id();
                        $at = $fields->coordinate();
                        $request = new PutMarker($id, $at, $fields->title($limits), $color);
                        break;
                    case 'note':
                        $fields->only('t', 'seq', 'kind', 'id', 'lat', 'lon', 'text');
                        $text = $fields->string('text');
                        TextRules::validateNote($text, $limits);
                        $id = $fields->id();
                        $request = new PutNote($id, $fields->coordinate(), $text);
                        break;
                    default:
                        throw new WireException(ErrorCode::InvalidMessage, "field 'kind' must be pin, marker or note");
                }
                break;
            case 'move':
                $fields->only('t', 'seq', 'id', 'lat', 'lon');
                $id = $fields->id();
                $request = new Move($id, $fields->coordinate());
                break;
            case 'delete':
                $fields->only('t', 'seq', 'id');
                $request = new Delete($fields->id());
                break;
            case 'delete_all':
                $fields->only('t', 'seq');
                $request = new DeleteAll();
                break;
            case 'photo_begin':
                $fields->only('t', 'seq', 'id', 'lat', 'lon', 'title', 'type', 'size', 'sha256', 'chunks');
                $photoType = PhotoType::tryFrom($fields->string('type'))
                    ?? throw new WireException(ErrorCode::PhotoInvalid, 'a photo is image/jpeg or image/png');
                $size = $fields->integer('size');
                $chunks = $fields->integer('chunks');
                $digest = $fields->string('sha256');
                if ($size < 1 || $chunks < 1) {
                    throw new WireException(ErrorCode::InvalidMessage, "fields 'size' and 'chunks' must be positive");
                }
                if ($size > $limits->photoBytes) {
                    throw new WireException(ErrorCode::TooLarge, "a photo is at most {$limits->photoBytes} bytes");
                }
                $chunkSize = IntegrationV1::MAX_CHUNK_BYTES;
                if ($chunks !== intdiv($size + $chunkSize - 1, $chunkSize)) {
                    throw new WireException(ErrorCode::InvalidMessage, "field 'chunks' does not match field 'size'");
                }
                if (!Hex::isFingerprint($digest)) {
                    throw new WireException(ErrorCode::InvalidMessage, "field 'sha256' is 64 lowercase hexadecimal digits");
                }
                $id = $fields->id();
                $at = $fields->coordinate();
                $request = new PhotoBegin(new PhotoHeader($id, $at, $fields->title($limits), $photoType, $size, $digest, $chunks));
                break;
            case 'photo_chunk':
                $fields->only('t', 'seq', 'id', 'index', 'data');
                $index = $fields->integer('index');
                if ($index < 0 || $index > 65534) {
                    throw new WireException(ErrorCode::InvalidMessage, "field 'index' is out of range");
                }
                $chunk = self::decodeCanonicalBase64($fields->string('data'))
                    ?? throw new WireException(ErrorCode::InvalidMessage, "field 'data' must be canonical base64");
                if ($chunk === '') {
                    throw new WireException(ErrorCode::InvalidMessage, 'a photo chunk cannot be empty');
                }
                if (strlen($chunk) > IntegrationV1::MAX_CHUNK_BYTES) {
                    throw new WireException(ErrorCode::TooLarge, 'a photo chunk is at most ' . IntegrationV1::MAX_CHUNK_BYTES . ' bytes');
                }
                $request = new PhotoChunk($fields->id(), $index, $chunk);
                break;
            case 'photo_end':
                $fields->only('t', 'seq', 'id');
                $request = new PhotoEnd($fields->id());
                break;
            case 'track_append':
                $fields->only('t', 'seq', 'id', 'title', 'points');
                $values = $fields->array('points');
                if (count($values) < 1 || count($values) > IntegrationV1::MAX_POINTS_PER_MESSAGE) {
                    throw new WireException(
                        ErrorCode::InvalidMessage,
                        "field 'points' has 1 to " . IntegrationV1::MAX_POINTS_PER_MESSAGE . ' entries',
                    );
                }
                $points = [];
                foreach ($values as $value) {
                    $point = new Fields($value);
                    $point->only('lat', 'lon', 'ts', 'alt');
                    $altitude = $point->has('alt') ? $point->number('alt') : null;
                    $coordinate = $point->coordinate();
                    $timestamp = $point->optionalInteger('ts');
                    $points[] = new TrackPoint($coordinate, $timestamp, $altitude);
                }
                $id = $fields->id();
                $request = new TrackAppend($id, $fields->title($limits), $points);
                break;
            case 'track_finish':
                $fields->only('t', 'seq', 'id');
                $request = new TrackFinish($fields->id());
                break;
            default:
                throw new WireException(ErrorCode::InvalidMessage, 'unknown message type');
        }
        return new RequestEnvelope($seq, $request);
    }

    /**
     * Base64 with padding and nothing else: no white space, no line breaks and no stray bits in the last character,
     * so that one chunk has exactly one spelling.
     */
    private static function decodeCanonicalBase64(string $text): ?string
    {
        if (strlen($text) % 4 !== 0 || strspn($text, 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=') !== strlen($text)) {
            return null;
        }
        $data = base64_decode($text, true);
        if ($data === false || base64_encode($data) !== $text) {
            return null;
        }
        return $data;
    }

    // Receiver to device

    public static function encodeReceiverMessage(ReceiverMessage $message): string
    {
        if ($message instanceof Welcome) {
            $limits = $message->limits;
            return JsonWriter::encode([
                't' => 'welcome',
                'version' => $message->version,
                'last_seq' => $message->lastSeq,
                'kinds' => array_map(static fn (ItemKind $kind): string => $kind->value, $message->kinds),
                'limits' => [
                    'items' => $limits->items,
                    'photos' => $limits->photos,
                    'photo_bytes' => $limits->photoBytes,
                    'note_bytes' => $limits->noteBytes,
                    'title_chars' => $limits->titleScalars,
                    'track_points' => $limits->trackPoints,
                    'open_tracks' => $limits->openTracks,
                    'tracks' => $limits->tracks,
                    'msgs_per_s' => $limits->messagesPerSecond,
                    'msg_burst' => $limits->messageBurst,
                    'points_per_s' => $limits->pointsPerSecond,
                    'point_burst' => $limits->pointBurst,
                    'disk_bytes' => $limits->diskBytes,
                ],
            ]);
        }
        if ($message instanceof Ack) {
            return JsonWriter::encode(['t' => 'ack', 'seq' => $message->seq, 'dup' => $message->duplicate]);
        }
        if ($message instanceof ReceiverError) {
            $fields = ['t' => 'error', 'code' => $message->code];
            if ($message->seq !== null) {
                $fields['seq'] = $message->seq;
            }
            if ($message->lastSeq !== null) {
                $fields['last_seq'] = $message->lastSeq;
            }
            if ($message->retryAfterMs !== null) {
                $fields['retry_after_ms'] = $message->retryAfterMs;
            }
            if ($message->supported !== null) {
                $fields['supported'] = $message->supported;
            }
            if ($message->fatal) {
                $fields['fatal'] = true;
            }
            return JsonWriter::encode($fields);
        }
        if ($message instanceof PairStatus) {
            $fields = ['t' => 'pair_status', 'status' => $message->status];
            if ($message->status === PairStatus::APPROVED) {
                $fields['kinds'] = array_map(static fn (ItemKind $kind): string => $kind->value, $message->kinds);
                $fields['counter'] = $message->counter;
            }
            return JsonWriter::encode($fields);
        }
        throw new \InvalidArgumentException('unknown message');
    }

    public static function decodeReceiverMessage(string $data): ReceiverMessage
    {
        $fields = self::parse($data);
        switch ($fields->string('t')) {
            case 'welcome':
                $fields->only('t', 'version', 'last_seq', 'kinds', 'limits');
                $version = $fields->integer('version');
                if ($version < 1 || $version > 65535) {
                    throw new WireException(ErrorCode::InvalidMessage, "field 'version' is out of range");
                }
                if (!$fields->has('limits')) {
                    throw new WireException(ErrorCode::InvalidMessage, "field 'limits' is missing");
                }
                $lastSeq = $fields->nonNegative('last_seq');
                $kinds = $fields->kinds();
                return new Welcome($version, $lastSeq, $kinds, self::decodeLimits($fields->map['limits']));
            case 'ack':
                $fields->only('t', 'seq', 'dup');
                $seq = $fields->seq();
                return new Ack($seq, $fields->bool('dup'));
            case 'error':
                $fields->only('t', 'code', 'seq', 'last_seq', 'retry_after_ms', 'supported', 'fatal');
                $code = $fields->string('code');
                if (preg_match('/^[a-z_]{1,64}$/D', $code) !== 1) {
                    throw new WireException(ErrorCode::InvalidMessage, "field 'code' is lowercase letters and underscores");
                }
                $versions = $fields->has('supported') ? $fields->array('supported') : null;
                // Empty is allowed: a receiver may have dropped every version this device could speak.
                if ($versions !== null && count($versions) > 16) {
                    throw new WireException(ErrorCode::InvalidMessage, "field 'supported' has at most 16 entries");
                }
                $supported = $versions === null ? null : array_map([self::class, 'version'], $versions);
                $seq = $fields->has('seq') ? $fields->seq() : null;
                $lastSeq = $fields->has('last_seq') ? $fields->nonNegative('last_seq') : null;
                $retry = $fields->has('retry_after_ms') ? $fields->nonNegative('retry_after_ms') : null;
                $fatal = $fields->has('fatal') && $fields->bool('fatal');
                return new ReceiverError($code, $seq, $lastSeq, $retry, $supported, $fatal);
            case 'pair_status':
                switch ($fields->string('status')) {
                    case 'pending':
                        $fields->only('t', 'status');
                        return PairStatus::pending();
                    case 'denied':
                        $fields->only('t', 'status');
                        return PairStatus::denied();
                    case 'approved':
                        $fields->only('t', 'status', 'kinds', 'counter');
                        $kinds = $fields->kinds();
                        return PairStatus::approved($kinds, $fields->nonNegative('counter'));
                    default:
                        throw new WireException(ErrorCode::InvalidMessage, 'unknown pairing status');
                }
            default:
                throw new WireException(ErrorCode::InvalidMessage, 'unknown message type');
        }
    }

    /** @internal */
    public static function version(mixed $value): int
    {
        if (!is_int($value) || $value < 1 || $value > 65535) {
            throw new WireException(ErrorCode::InvalidMessage, 'a version is an integer within 1...65535');
        }
        return $value;
    }

    private static function decodeLimits(mixed $value): Limits
    {
        $fields = new Fields($value);
        $fields->only(
            'items', 'photos', 'photo_bytes', 'note_bytes', 'title_chars', 'track_points', 'open_tracks', 'tracks',
            'msgs_per_s', 'msg_burst', 'points_per_s', 'point_burst', 'disk_bytes',
        );
        $positive = static function (string $key) use ($fields): int {
            $number = $fields->integer($key);
            if ($number < 1 || $number > (1 << 40)) {
                throw new WireException(ErrorCode::InvalidMessage, 'a limit is out of range');
            }
            return $number;
        };
        // A device may batch this many points, so a smaller burst could never be satisfied.
        if ($positive('point_burst') < IntegrationV1::MAX_POINTS_PER_MESSAGE) {
            throw new WireException(ErrorCode::InvalidMessage, "field 'point_burst' is below the maximum batch size");
        }
        return new Limits(
            $positive('items'),
            $positive('photos'),
            $positive('photo_bytes'),
            $positive('note_bytes'),
            $positive('title_chars'),
            $positive('track_points'),
            $positive('open_tracks'),
            $positive('tracks'),
            $positive('msgs_per_s'),
            $positive('msg_burst'),
            $positive('points_per_s'),
            $positive('point_burst'),
            $positive('disk_bytes'),
        );
    }

    private static function parse(string $data): Fields
    {
        if (strlen($data) > IntegrationV1::MAX_PLAINTEXT_LENGTH) {
            throw new WireException(ErrorCode::TooLarge, 'a message is at most 65519 bytes');
        }
        try {
            $value = StrictJson::parse($data);
        } catch (JsonParseException) {
            throw new WireException(ErrorCode::InvalidMessage, 'the message is not valid strict JSON');
        }
        return new Fields($value);
    }
}
