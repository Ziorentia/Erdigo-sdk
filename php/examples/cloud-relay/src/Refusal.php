<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * An answer other than success: an HTTP status and the JSON object to send with it.
 */
final class Refusal extends \RuntimeException
{
    /**
     * @param array<string, string|int> $body
     * @param array<string, string> $headers
     */
    public function __construct(public readonly int $status, public readonly array $body, public readonly array $headers = [])
    {
        parent::__construct((string) ($body['error'] ?? 'error'));
    }
}
