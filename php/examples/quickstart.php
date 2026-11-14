<?php

declare(strict_types=1);

/*
 * A complete small program: pairs once with the QR text from the Erdigo app, then writes a pin, a note and a track.
 *
 *     php examples/quickstart.php 192.168.1.20 52345 /path/to/qr-text-file [STATE_FOLDER]
 *
 * The QR text file holds the text of the app's pairing QR code, and must be readable by you alone (chmod 600). It is
 * needed only the first time. Without the app, run a mock receiver of the .NET or Swift edition and give its port and
 * its --code-file. The pairing is kept in STATE_FOLDER, or in a profile of its own by default.
 */

require __DIR__ . '/../autoload.php';

use Erdigo\Core\Coordinate;
use Erdigo\Core\ItemKind;
use Erdigo\Core\PairingTicket;
use Erdigo\Core\TrackPoint;
use Erdigo\ErdigoClient;
use Erdigo\ErdigoException;
use Erdigo\FileDeviceStore;
use Erdigo\Pairing;
use Erdigo\PrivateFile;
use Erdigo\ReceiverTarget;

[$host, $port, $codeFile, $stateFolder] = array_slice($argv, 1) + [null, null, null, null];
if ($host === null || $port === null) {
    fwrite(STDERR, "usage: php quickstart.php HOST PORT [QR_TEXT_FILE [STATE_FOLDER]]\n");
    exit(2);
}
$target = ReceiverTarget::address($host, (int) $port);

try {
    $store = new FileDeviceStore($stateFolder ?? FileDeviceStore::defaultDirectory('quickstart'));
    $identity = $store->loadOrCreateIdentity();

    $pairings = $store->pairings();
    if ($pairings === []) {
        if ($codeFile === null) {
            fwrite(STDERR, "Not paired yet: give the file with the QR text.\n");
            exit(2);
        }
        // The QR text carries the receiver's key, so it is compared without asking anyone.
        $ticket = PairingTicket::parse(PrivateFile::readAllBytes($codeFile, 1024));
        echo "Confirm the request in the Erdigo app...\n";
        $pairing = Pairing::pair($target, $ticket, $identity, 'PHP quickstart', [ItemKind::Pin, ItemKind::Note, ItemKind::Track]);
        $store->save($pairing);
        $pairings = [$pairing];
    }

    $client = new ErdigoClient($identity, $pairings[0], $store, $target);
    $spot = new Coordinate(37.5663, 126.9779);
    $client->putPin('city-hall', $spot, 'Seoul City Hall');
    $client->putNote('reminder', $spot, 'Sent from PHP.');
    $client->appendTrack('walk', [
        new TrackPoint($spot, (int) (microtime(true) * 1000)),
        new TrackPoint(new Coordinate(37.5665, 126.9780), (int) (microtime(true) * 1000) + 1000),
    ], 'Short walk');
    $client->finishTrack('walk');
    $client->close();
    echo "Sent a pin, a note and a track.\n";
} catch (ErdigoException $error) {
    fwrite(STDERR, $error->getMessage() . "\n");
    exit(1);
} catch (\Erdigo\PrivateFileException $error) {
    fwrite(STDERR, 'The QR text file: ' . $error->getMessage() . "\n");
    exit(1);
}
