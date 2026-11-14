// Multicast DNS messages and what a browse keeps from the answers, as the .NET discovery tests check them.

import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { test } from 'node:test';
import { DiscoveredServices, ReceiverTarget, browse, candidates, knownEndpoints } from '../src/discovery.js';
import { DnsType, SERVICE_NAME, decodeDns, encodeDns, isLocalAddress } from '../src/dns.js';
import { ErdigoError } from '../src/identity.js';

const INSTANCE = '0123456789abcdef._erdigo-int._tcp.local';
const HOST = 'erdigo-0123456789ab.local';

function answer() {
  return encodeDns({
    id: 7,
    isResponse: true,
    authoritative: true,
    questions: [{ name: SERVICE_NAME, type: DnsType.ptr }],
    answers: [{ name: SERVICE_NAME, type: DnsType.ptr, ttl: 10, cacheFlush: false, target: INSTANCE }],
    additionals: [
      { name: INSTANCE, type: DnsType.srv, ttl: 10, cacheFlush: true, target: HOST, port: 4242 },
      { name: INSTANCE, type: DnsType.txt, ttl: 10, cacheFlush: true, text: ['v=1'] },
      { name: HOST, type: DnsType.a, ttl: 10, cacheFlush: true, address: '192.0.2.10' },
    ],
  });
}

test('messages round trip', () => {
  const message = decodeDns(answer());
  assert.ok(message);
  assert.ok(message.isResponse);
  assert.equal(message.id, 7);
  assert.equal(message.questions[0].name, SERVICE_NAME);
  assert.equal(message.answers[0].target, INSTANCE);
  const service = message.additionals.find((record) => record.type === DnsType.srv);
  assert.deepEqual([service?.target, service?.port], [HOST, 4242]);
  assert.deepEqual(message.additionals.find((record) => record.type === DnsType.txt)?.text, ['v=1']);
  assert.equal(message.additionals.find((record) => record.type === DnsType.a)?.address, '192.0.2.10');
});

test('compressed names are read', () => {
  // A PTR answer whose target points back at the question's name, as real responders write it.
  const bytes = Buffer.from([
    0, 1, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0,
    3, 0x61, 0x62, 0x63, 5, 0x6c, 0x6f, 0x63, 0x61, 0x6c, 0, 0, 12, 0, 1,
    0xc0, 12, 0, 12, 0, 1, 0, 0, 0, 10, 0, 6, 3, 0x78, 0x79, 0x7a, 0xc0, 12,
  ]);
  const message = decodeDns(bytes);
  assert.equal(message?.answers[0].name, 'abc.local');
  assert.equal(message?.answers[0].target, 'xyz.abc.local');
});

test('hostile messages are refused', () => {
  const good = answer();
  // Every truncation of a valid message is refused rather than read past its end.
  for (let length = 0; length < good.length; length += 1) {
    assert.equal(decodeDns(good.subarray(0, length)), null, `length ${length}`);
  }
  // A name pointer that points at itself, or forwards, would loop or read ahead.
  assert.equal(decodeDns(Buffer.from([0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 12, 0, 12, 0, 1])), null);
  assert.equal(decodeDns(Buffer.from([0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 20, 0, 12, 0, 1, 0, 0])), null);
  // A label type this reader does not know.
  assert.equal(decodeDns(Buffer.from([0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x40, 0, 0, 12, 0, 1])), null);
  // A name longer than 255 bytes.
  const label = Buffer.concat([Buffer.from([63]), Buffer.alloc(63, 0x61)]);
  const longName = Buffer.concat([Buffer.from([0, 1, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0]), label, label, label, label, label, Buffer.from([0, 0, 12, 0, 1])]);
  assert.equal(decodeDns(longName), null);
  // A record that claims more data than there is.
  const claim = Buffer.from(good);
  claim[claim.length - 6] = 0x00;
  claim[claim.length - 5] = 0xff;
  assert.equal(decodeDns(claim), null);
  // An error response is of no use.
  const failed = Buffer.from(good);
  failed[3] |= 0x03;
  assert.equal(decodeDns(failed), null);
});

test('random bytes never throw', () => {
  for (let round = 0; round < 20_000; round += 1) {
    const bytes = randomBytes(Math.floor(Math.random() * 300));
    if (bytes.length > 4) {
      bytes[2] &= 0x87;
      bytes[3] &= 0xf0;
    }
    decodeDns(bytes);
  }
});

test('the local network is recognised', () => {
  assert.ok(isLocalAddress('127.0.0.1'));
  assert.ok(isLocalAddress('169.254.10.20'));
  assert.ok(isLocalAddress('fe80::1'));
  assert.ok(isLocalAddress('fe80::1%en0'));
  assert.ok(isLocalAddress('::ffff:127.0.0.1'));
  assert.ok(!isLocalAddress('8.8.8.8'));
  assert.ok(!isLocalAddress('2001:db8::1'));
  assert.ok(!isLocalAddress('not an address'));
});

test('an address needs no discovery', async () => {
  const target = ReceiverTarget.address('127.0.0.1', 4242);
  assert.deepEqual(knownEndpoints(target, null), [{ host: '127.0.0.1', port: 4242 }]);
  assert.deepEqual(await candidates(target), [{ host: '127.0.0.1', port: 4242 }]);
  assert.throws(() => ReceiverTarget.address('127.0.0.1', 0), ErdigoError);
  const remembered = { label: 'x', receiverKey: Buffer.alloc(32), kinds: ['pin'], lastHost: '192.0.2.1', lastPort: 7, pairedAt: new Date(), counterFloor: 0 };
  assert.deepEqual(knownEndpoints(ReceiverTarget.discover, /** @type {any} */ (remembered)), [{ host: '192.0.2.1', port: 7 }]);
});

const FROM_V4 = '192.0.2.1';
const FROM_V6 = 'fe80::2%5';
const ptr = (/** @type {string} */ instance, ttl = 10) => ({ name: SERVICE_NAME, type: DnsType.ptr, ttl, cacheFlush: false, target: instance });
const srv = (/** @type {string} */ instance, /** @type {string} */ host, /** @type {number} */ port) => ({ name: instance, type: DnsType.srv, ttl: 10, cacheFlush: true, target: host, port });
const address = (/** @type {string} */ host, /** @type {string} */ value) => ({ name: host, type: value.includes(':') ? DnsType.aaaa : DnsType.a, ttl: 10, cacheFlush: true, address: value });
const instanceNamed = (/** @type {number} */ number) => `${number.toString(16).padStart(16, '0')}._erdigo-int._tcp.local`;
const random = (/** @type {number} */ below) => Math.floor(Math.random() * below);

test('many repeated and unasked records keep a bounded amount', () => {
  const found = new DiscoveredServices(8);
  for (let round = 0; round < 5000; round += 1) {
    const records = [
      ptr(INSTANCE),
      srv(INSTANCE, HOST, 4242),
      { name: INSTANCE, type: DnsType.txt, ttl: 10, cacheFlush: true, text: ['v=1'] },
      address(HOST, '192.0.2.10'),
      address(HOST, `10.0.${random(256)}.${random(256)}`),
      address(HOST, `2001:db8::${random(65536).toString(16)}`),
      // Names nothing asked about.
      srv(`other${round}._erdigo-int._tcp.local`, `other${round}.local`, 1),
      address(`other${round}.local`, '192.0.2.99'),
    ];
    if (round % 10 === 0) {
      // More instances than are wanted, each complete.
      records.push(ptr(instanceNamed(round)), srv(instanceNamed(round), `host${round}.local`, 1000), address(`host${round}.local`, '192.0.2.20'));
    }
    found.add(records, FROM_V4);
  }
  assert.equal(found.instances.length, 8);
  assert.ok(found.keptRecords >= 8 && found.keptRecords <= 8 * (1 + 1 + 2 * 4), String(found.keptRecords));
  const endpoints = found.endpoints();
  assert.equal(endpoints.length, 8);
  // The first address heard for the first receiver is still the one used.
  assert.deepEqual(endpoints[0], { host: '192.0.2.10', port: 4242 });
});

test('several receivers are found in the order they answered', () => {
  const found = new DiscoveredServices(8);
  const b = [ptr('b._erdigo-int._tcp.local'), srv('b._erdigo-int._tcp.local', 'b.local', 2000), address('b.local', '192.0.2.2')];
  found.add(b, FROM_V4);
  found.add([ptr('a._erdigo-int._tcp.local'), srv('a._erdigo-int._tcp.local', 'a.local', 1000), address('a.local', '192.0.2.1')], FROM_V4);
  // The same answer again changes nothing.
  found.add(b, FROM_V4);
  assert.deepEqual(found.endpoints(), [{ host: '192.0.2.2', port: 2000 }, { host: '192.0.2.1', port: 1000 }]);
  assert.equal(found.keptRecords, 6);
});

test('later answers complete earlier ones', () => {
  const found = new DiscoveredServices(8);
  // Names differ in case and in a final dot, as they may between responders.
  found.add([ptr(`${INSTANCE}.`)], FROM_V4);
  assert.deepEqual(found.instancesWithoutService(), [INSTANCE]);
  found.closeInstances();
  // The answer to the question about the service, with an instance nobody asked about.
  found.add([srv(INSTANCE.toUpperCase(), HOST, 4242), ptr('late._erdigo-int._tcp.local')], FROM_V4);
  assert.deepEqual(found.instancesWithoutService(), []);
  assert.deepEqual(found.hostsWithoutAddress(), [HOST]);
  assert.deepEqual(found.endpoints(), []);
  // The answer to the question about the host's addresses.
  found.add([address(`${HOST.toUpperCase()}.`, '192.0.2.10')], FROM_V4);
  assert.deepEqual(found.hostsWithoutAddress(), []);
  assert.deepEqual(found.endpoints(), [{ host: '192.0.2.10', port: 4242 }]);
  assert.deepEqual(found.instances, [INSTANCE]);
});

test('records nothing led to are not kept, and are asked for later', () => {
  const found = new DiscoveredServices(8);
  // A service and an address that arrive before the record pointing at them.
  found.add([srv(INSTANCE, HOST, 4242), address(HOST, '192.0.2.10')], FROM_V4);
  assert.equal(found.keptRecords, 0);
  found.add([ptr(INSTANCE)], FROM_V4);
  assert.deepEqual(found.instancesWithoutService(), [INSTANCE]);
});

test('instances stop at the limit, and goodbyes are ignored', () => {
  const found = new DiscoveredServices(3);
  found.add([ptr(instanceNamed(0), 0)], FROM_V4);
  found.add(Array.from({ length: 20 }, (_, index) => ptr(instanceNamed(index + 1))), FROM_V4);
  assert.deepEqual(found.instances, [instanceNamed(1), instanceNamed(2), instanceNamed(3)]);
  assert.equal(found.keptRecords, 3);
});

test('a service without a port does not hide a later one', () => {
  const found = new DiscoveredServices(8);
  found.add([ptr(INSTANCE), srv(INSTANCE, HOST, 0)], FROM_V4);
  found.add([srv(INSTANCE, HOST, 4242), srv(INSTANCE, 'elsewhere.local', 1), address(HOST, '192.0.2.10')], FROM_V4);
  assert.deepEqual(found.endpoints(), [{ host: '192.0.2.10', port: 4242 }]);
});

test('IPv6 link-local addresses keep the interface they came from', () => {
  const found = new DiscoveredServices(8);
  found.add([ptr(INSTANCE), srv(INSTANCE, HOST, 4242)], FROM_V4);
  // Over IPv4 the interface is unknown, so a link-local address is of no use.
  found.add([address(HOST, 'fe80::10')], FROM_V4);
  assert.deepEqual(found.hostsWithoutAddress(), [HOST]);
  // Over IPv6 it takes the interface the answer came in on, and the same address on two interfaces is two ways in.
  found.add([address(HOST, 'fe80::10'), address(HOST, 'fe80::10')], FROM_V6);
  found.add([address(HOST, 'fe80::10')], 'fe80::3%7');
  assert.deepEqual(found.endpoints(), [{ host: 'fe80::10%5', port: 4242 }]);
  assert.equal(found.keptRecords, 1 + 1 + 2);
  // An IPv4 address is still preferred when there is one, wherever it was heard.
  found.add([address(HOST, '192.0.2.10')], FROM_V6);
  assert.deepEqual(found.endpoints(), [{ host: '192.0.2.10', port: 4242 }]);
});

test('a stopped browse stops', async () => {
  const stopped = new AbortController();
  stopped.abort(new Error('stopped'));
  await assert.rejects(browse({ signal: stopped.signal }), /stopped/);
  // Stopping partway does not wait out the time given for answers.
  const later = AbortSignal.timeout(200);
  const started = Date.now();
  try {
    assert.deepEqual(await browse({ durationMs: 30_000, signal: later }), []);
  } catch (error) {
    assert.equal(/** @type {Error} */ (error).name, 'TimeoutError');
  }
  assert.ok(Date.now() - started < 10_000, `took ${Date.now() - started} ms`);
});
