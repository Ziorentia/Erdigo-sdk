#!/usr/bin/env node
// A complete small program: pair with a receiver once, then write a few items.
//
// Start a mock receiver in one terminal, from the .NET or the Swift SDK:
//
//     dotnet run --project windows/src/erdigo-send -- serve --mock
//
// and run this in another, using the port and code it prints:
//
//     node examples/quickstart.js 127.0.0.1 <port>
//
// The code is read from the keyboard. It is never passed as a command-line argument, because arguments end up in the
// process list and in shell history. If the code was typed rather than copied from the QR text, you then compare the
// receiver's key and type yes. The receiver is not asked to pair, no pairing is saved and no items are sent before
// that. That needs standard input and output to be a terminal. Without one, a typed code is refused, so use the QR
// text.

import { createInterface } from 'node:readline';
import { isatty } from 'node:tty';
import {
  ErdigoClient,
  ErdigoError,
  InMemoryDeviceStore,
  PairingTicket,
  ReceiverTarget,
  WireError,
  pair,
} from '../src/index.js';
import { ask, readSecretLine } from '../src/cli/terminal.js';

/** Reads the code without showing it. With no terminal, it reads a line from standard input, which is how QR text
 * reaches a program that runs without one. */
async function readCode() {
  if (!isatty(0)) {
    const lines = createInterface({ input: process.stdin });
    for await (const line of lines) {
      lines.close();
      return line;
    }
    return '';
  }
  return (await readSecretLine('Pairing code: ', 1024)) ?? '';
}

async function run(/** @type {import('../src/index.js').ReceiverTarget} */ target) {
  // A real program would use FileDeviceStore, so the pairing survives a restart.
  const store = new InMemoryDeviceStore();
  const identity = store.loadOrCreateIdentity();
  const ticket = PairingTicket.parse(await readCode());
  // Only a person can compare the key. Whatever a pipe or a file holds was written before the key existed, so it is
  // no answer, and a question sent to a file would be answered unseen. The receiver is not contacted when no answer
  // is possible.
  if (ticket.fingerprint === null && !(isatty(0) && isatty(1))) {
    console.error("A typed code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text.");
    process.exit(1);
  }
  console.log('Contacting the receiver. If it asks, confirm the request there.');
  const pairing = await pair({
    target,
    ticket,
    identity,
    deviceName: 'Quickstart example',
    kinds: ['pin', 'marker', 'note', 'track'],
    // A typed code proves only that the other side knows the code, so a person has to compare the receiver's key.
    // This runs before the receiver is asked to pair. A QR code carries the key, and pair compares it without asking.
    confirmKey: async (key) => {
      // Only an answer given after seeing the key counts, so anything typed before it is discarded. End of input and
      // every other answer count as no.
      const answer = await ask([`Receiver key: ${key}`, 'If this matches the key shown on the receiver, type yes: ']);
      const confirmed = answer?.trim().toLowerCase() === 'yes';
      if (!confirmed) {
        console.log();
      }
      return confirmed;
    },
  });
  store.save(pairing);

  const client = new ErdigoClient({ identity, pairing, store, target });
  // Each request keeps trying for 30 seconds after a connection is lost, and that time starts again whenever a
  // connection is made. A deadline limits the whole run, however the connection behaves.
  const signal = AbortSignal.timeout(120_000);
  try {
    const cityHall = { lat: 37.5663, lon: 126.9779 };
    await client.putPin('city-hall', cityHall, { title: 'Seoul City Hall', signal });
    const palace = { lat: 37.5796, lon: 126.977 };
    await client.putMarker('palace', palace, { title: 'Gyeongbokgung Palace', color: 'red', signal });
    await client.putNote('meeting-point', palace, 'Meet at the main gate.', { signal });
    // Walk from the palace to City Hall in a straight line.
    const points = [0, 1, 2, 3, 4].map((step) => ({
      lat: palace.lat + ((cityHall.lat - palace.lat) * step) / 4,
      lon: palace.lon + ((cityHall.lon - palace.lon) * step) / 4,
    }));
    await client.appendTrack('walk', points, { title: 'Walk to City Hall', signal });
    await client.finishTrack('walk', { signal });
  } finally {
    client.close();
  }
  console.log('Done.');
}

const [host, portText] = process.argv.slice(2);
if (!host || !/^\d{1,5}$/.test(portText ?? '') || Number(portText) < 1 || Number(portText) > 65535) {
  console.log('usage: node examples/quickstart.js <host> <port>');
  process.exit(0);
}
try {
  await run(ReceiverTarget.address(host, Number(portText)));
} catch (error) {
  if (error instanceof ErdigoError || error instanceof WireError) {
    console.log(`Failed: ${error.message}`);
  } else if (error instanceof Error && error.name === 'TimeoutError') {
    console.log('Failed: the receiver did not confirm everything within two minutes.');
  } else {
    throw error;
  }
  process.exitCode = 1;
}
