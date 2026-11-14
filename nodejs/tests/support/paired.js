// Starts a test receiver and pairs a device with it, for the tests that need a working connection.

import { ErdigoClient, RetryPolicy } from '../../src/client.js';
import { ReceiverTarget } from '../../src/discovery.js';
import { DeviceIdentity } from '../../src/identity.js';
import { ITEM_KINDS, PairingTicket } from '../../src/model.js';
import { pair } from '../../src/pairing.js';
import { InMemoryDeviceStore } from '../../src/store.js';
import { TestReceiver } from './receiver.js';

/**
 * @param {object} [options]
 * @param {import('../../src/client.js').RetryPolicy} [options.retry]
 * @param {ConstructorParameters<typeof TestReceiver>[0]} [options.receiver]
 * @param {string[]} [options.kinds]
 */
export async function pairedDevice({ retry = RetryPolicy.upTo(10_000), receiver: receiverOptions, kinds = [...ITEM_KINDS] } = {}) {
  const receiver = new TestReceiver(receiverOptions);
  const port = await receiver.start();
  const identity = DeviceIdentity.generate();
  const store = new InMemoryDeviceStore(identity);
  const target = ReceiverTarget.address('127.0.0.1', port);
  const pairing = await pair({ target, ticket: PairingTicket.parse(receiver.qrText), identity, deviceName: 'Test device', kinds });
  store.save(pairing);
  const client = new ErdigoClient({ identity, pairing, store, target, retry, connectTimeoutMs: 2000, ackTimeoutMs: 5000 });
  const done = async () => {
    client.close();
    await receiver.stop();
  };
  return { receiver, port, identity, store, pairing, client, target, done };
}
