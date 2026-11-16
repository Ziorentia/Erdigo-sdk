<?php

declare(strict_types=1);

/*
 * For tests/cloud-relay-smoke.sh: sends one request to a relay on this computer, and prints the status and the body
 * on one line. The token comes from TEST_RELAY_TOKEN, so that it never appears in a process list.
 *
 *     TEST_RELAY_TOKEN=... php cloud-relay-http.php post PORT < body.json
 *     TEST_RELAY_TOKEN=... php cloud-relay-http.php get PORT [QUERY]
 */

[$command, $port, $query] = array_slice($argv, 1) + [null, null, ''];
$token = (string) getenv('TEST_RELAY_TOKEN');
$socket = stream_socket_client('tcp://127.0.0.1:' . (int) $port, $errorNumber, $errorText, 5);
if ($socket === false) {
    echo "0 unreachable\n";
    exit(1);
}
stream_set_timeout($socket, 60);
if ($command === 'post') {
    $body = (string) stream_get_contents(STDIN);
    $request = "POST /v1/ops HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {$token}\r\nContent-Type: application/json\r\n"
        . 'Content-Length: ' . strlen($body) . "\r\nConnection: close\r\n\r\n" . $body;
} else {
    $target = '/v1/ops' . ($query === '' ? '' : "?{$query}");
    $request = "GET {$target} HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {$token}\r\nConnection: close\r\n\r\n";
}
fwrite($socket, $request);
$answer = (string) stream_get_contents($socket);
fclose($socket);
$end = strpos($answer, "\r\n\r\n");
preg_match('#^HTTP/1\.[01] ([0-9]{3})#', $answer, $match);
echo ($match[1] ?? '0') . ' ' . ($end === false ? '' : substr($answer, $end + 4)) . "\n";
