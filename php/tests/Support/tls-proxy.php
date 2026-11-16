<?php

declare(strict_types=1);

/*
 * For tests/cloud-relay-smoke.sh: an HTTPS front for a relay on this computer, as a reverse proxy would be. It makes a
 * self-signed certificate for "localhost" in FOLDER (cert.pem, key.pem), listens on a loopback port the system picks,
 * prints "port N", and passes each request to the relay's plain HTTP port, one at a time.
 *
 *     php tls-proxy.php FOLDER RELAY_PORT
 */

[$folder, $relayPort] = array_slice($argv, 1) + [null, null];
$key = openssl_pkey_new(['private_key_type' => OPENSSL_KEYTYPE_EC, 'curve_name' => 'prime256v1']);
$request = openssl_csr_new(['commonName' => 'localhost'], $key, ['digest_alg' => 'sha256']);
$certificate = openssl_csr_sign($request, null, $key, 1, ['digest_alg' => 'sha256']);
if ($key === false || $request === false || $certificate === false) {
    fwrite(STDERR, "tls-proxy: could not make a certificate\n");
    exit(1);
}
openssl_x509_export_to_file($certificate, "{$folder}/cert.pem");
openssl_pkey_export_to_file($key, "{$folder}/key.pem");
chmod("{$folder}/key.pem", 0600);

$context = stream_context_create(['ssl' => [
    'local_cert' => "{$folder}/cert.pem",
    'local_pk' => "{$folder}/key.pem",
    'verify_peer' => false,
]]);
$server = stream_socket_server('tls://127.0.0.1:0', $errorNumber, $errorText, STREAM_SERVER_BIND | STREAM_SERVER_LISTEN, $context);
if ($server === false) {
    fwrite(STDERR, "tls-proxy: could not listen\n");
    exit(1);
}
$name = (string) stream_socket_get_name($server, false);
echo 'port ' . substr((string) strrchr($name, ':'), 1) . "\n";
fflush(STDOUT);

while (true) {
    // A client that refuses the certificate ends its handshake here, and is simply not served.
    $client = @stream_socket_accept($server, 3600);
    if ($client === false) {
        continue;
    }
    stream_set_timeout($client, 60);
    $head = '';
    while (!str_contains($head, "\r\n\r\n") && !feof($client) && strlen($head) < 65536) {
        $chunk = fread($client, 8192);
        if ($chunk === false || $chunk === '') {
            break;
        }
        $head .= $chunk;
    }
    $upstream = @stream_socket_client('tcp://127.0.0.1:' . (int) $relayPort, $errorNumber, $errorText, 5);
    if ($upstream !== false) {
        fwrite($upstream, $head);
        while (!feof($upstream)) {
            $chunk = fread($upstream, 65536);
            if ($chunk === false || $chunk === '') {
                break;
            }
            fwrite($client, $chunk);
        }
        fclose($upstream);
    }
    fclose($client);
}
