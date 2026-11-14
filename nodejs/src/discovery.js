// Finds receivers that advertise `_erdigo-int._tcp`, with one-shot multicast DNS queries (RFC 6762 section 5.1) sent
// from an ordinary port. Receivers answer such a query directly, so nothing here needs port 5353, and nothing here is
// used when an address is given.

import { randomInt } from 'node:crypto';
import { createSocket } from 'node:dgram';
import { isIP } from 'node:net';
import { networkInterfaces } from 'node:os';
import { setTimeout as delay } from 'node:timers/promises';
import { DnsType, MDNS_PORT, MULTICAST_V4, MULTICAST_V6, SERVICE_NAME, decodeDns, encodeDns, isLocalAddress, namesEqual, trimDot } from './dns.js';
import { ErdigoError, ErdigoErrorKind } from './identity.js';

/**
 * Where to look for a receiver.
 * @typedef {{ kind: 'discover' } | { kind: 'address', host: string, port: number }} ReceiverTarget
 */

export const ReceiverTarget = Object.freeze({
  /**
   * Try the remembered address, then up to eight receivers advertised on the local network. A receiver this device is
   * not paired with stays silent. An advertisement proves nothing about who is behind it, so use {@link address}
   * where that matters. See the threat model.
   * @type {ReceiverTarget}
   */
  discover: Object.freeze({ kind: 'discover' }),

  /**
   * Try this address only.
   * @param {string} host
   * @param {number} port
   * @returns {ReceiverTarget}
   */
  address(host, port) {
    if (typeof host !== 'string' || host.length === 0) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the host cannot be empty' });
    }
    return Object.freeze({ kind: 'address', host, port: validPort(port) });
  },
});

/** @param {number} port */
function validPort(port) {
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the port must be between 1 and 65535' });
  }
  return port;
}

/** @typedef {{ host: string, port: number }} ReceiverEndpoint */

/**
 * The address given or remembered, which can be tried without looking around the network.
 * @param {ReceiverTarget} target
 * @param {import('./identity.js').ReceiverPairing | null} pairing
 * @returns {ReceiverEndpoint[]}
 */
export function knownEndpoints(target, pairing) {
  if (target.kind === 'address') {
    return [{ host: target.host, port: validPort(target.port) }];
  }
  if (pairing?.lastHost && pairing.lastPort) {
    return [{ host: pairing.lastHost, port: validPort(pairing.lastPort) }];
  }
  return [];
}

/**
 * Every endpoint to try when pairing, where no address is remembered.
 * @param {ReceiverTarget} target
 * @param {AbortSignal} [signal]
 */
export async function candidates(target, signal) {
  return target.kind === 'discover' ? browse({ signal }) : knownEndpoints(target, null);
}

/** Addresses kept for each host and address family. A receiver has one or two of each. */
export const ADDRESSES_PER_FAMILY = 4;

/**
 * What the answers to one browse have said, kept to what the browse can use. Each answer is read in order of
 * dependence: service instances, then their services, then the addresses of those services' hosts. So a later answer
 * can complete an earlier one, and a record nothing has asked about is not kept. However many answers arrive, and
 * however often they repeat themselves, this holds at most `limit` instances, one service for each, and a few
 * addresses for each of their hosts.
 */
export class DiscoveredServices {
  /** @param {number} limit */
  constructor(limit) {
    this.limit = limit;
    /** @type {string[]} */
    this.instances = [];
    /** @type {Map<string, { target: string, port: number }>} */
    this.services = new Map();
    /** @type {Map<string, string[]>} */
    this.addresses = new Map();
    this.instancesClosed = false;
  }

  /** @param {string} name */
  static key(name) {
    return trimDot(name).toLowerCase();
  }

  /** How many records are kept, for checking that this stays bounded. */
  get keptRecords() {
    let count = this.instances.length + this.services.size;
    for (const list of this.addresses.values()) {
      count += list.length;
    }
    return count;
  }

  /**
   * Takes the records of one answer, which came from `sender`.
   * @param {import('./dns.js').DnsRecord[]} records
   * @param {string} sender
   */
  add(records, sender) {
    const key = DiscoveredServices.key;
    const senderIsV6 = isIP(sender.split('%')[0]) === 6;
    const scope = sender.includes('%') ? sender.slice(sender.indexOf('%')) : '';
    /** @type {import('./dns.js').DnsRecord[]} */
    const usable = [];
    for (const record of records) {
      // An IPv6 link-local address works only on the interface it was learned on, which is known only when the answer
      // itself came over IPv6.
      if (record.address && /^fe[89ab]/i.test(record.address)) {
        if (senderIsV6 && scope) {
          usable.push({ ...record, address: `${record.address}${scope}` });
        }
      } else {
        usable.push(record);
      }
    }
    if (!this.instancesClosed) {
      for (const record of usable) {
        if (this.instances.length < this.limit && record.type === DnsType.ptr && namesEqual(record.name, SERVICE_NAME)
          && record.target && record.ttl > 0 && !this.instances.includes(key(record.target))) {
          this.instances.push(key(record.target));
        }
      }
    }
    for (const record of usable) {
      // The first usable service for an instance stands, as a resolver would keep the first it heard.
      if (record.type === DnsType.srv && record.target && record.port && this.instances.includes(key(record.name))
        && !this.services.has(key(record.name))) {
        this.services.set(key(record.name), { target: key(record.target), port: record.port });
      }
    }
    const hosts = new Set([...this.services.values()].map((service) => service.target));
    for (const record of usable) {
      if (!record.address || !hosts.has(key(record.name))) {
        continue;
      }
      const known = this.addresses.get(key(record.name)) ?? [];
      this.addresses.set(key(record.name), known);
      const family = isIP(record.address.split('%')[0]);
      // The same address with another interface is another way to reach the host, so it is kept as well.
      if (!known.includes(record.address) && known.filter((each) => isIP(each.split('%')[0]) === family).length < ADDRESSES_PER_FAMILY) {
        known.push(record.address);
      }
    }
  }

  /** From now on no new instances are taken, only what completes the ones already found. */
  closeInstances() {
    this.instancesClosed = true;
  }

  instancesWithoutService() {
    return this.instances.filter((instance) => !this.services.has(instance));
  }

  hostsWithoutAddress() {
    return [...new Set([...this.services.values()].map((service) => service.target))].filter((host) => !this.addresses.has(host));
  }

  /**
   * One address for each instance that has a service and an address, in the order the instances were found.
   * @returns {ReceiverEndpoint[]}
   */
  endpoints() {
    /** @type {ReceiverEndpoint[]} */
    const endpoints = [];
    for (const instance of this.instances) {
      const service = this.services.get(instance);
      const known = service ? this.addresses.get(service.target) : undefined;
      if (!service || !known || known.length === 0) {
        continue;
      }
      // IPv4 first, because an IPv6 link-local address needs the right interface to work.
      const address = [...known].sort((left, right) => (isIP(left) === 4 ? 0 : 1) - (isIP(right) === 4 ? 0 : 1))[0];
      endpoints.push({ host: address, port: service.port });
    }
    return endpoints;
  }
}

/**
 * Listens for receivers for a short time, and returns up to `limit` of them.
 * @param {{ durationMs?: number, limit?: number, signal?: AbortSignal }} [options]
 * @returns {Promise<ReceiverEndpoint[]>}
 */
export async function browse({ durationMs = 2000, limit = 8, signal } = {}) {
  const found = new DiscoveredServices(limit);
  await query([{ name: SERVICE_NAME, type: DnsType.ptr }], durationMs, found, signal);
  // Answers to the questions below only fill in what this first one found.
  found.closeInstances();
  if (found.instances.length === 0) {
    return [];
  }
  // A responder normally sends the service and its addresses along with the answer. Ask for whatever it did not.
  const missingServices = found.instancesWithoutService();
  if (missingServices.length > 0) {
    await query(missingServices.map((instance) => ({ name: instance, type: DnsType.srv })), 1000, found, signal);
  }
  const missingAddresses = found.hostsWithoutAddress();
  if (missingAddresses.length > 0) {
    await query(missingAddresses.flatMap((host) => [{ name: host, type: DnsType.a }, { name: host, type: DnsType.aaaa }]), 1000, found, signal);
  }
  return found.endpoints();
}

/**
 * Sends one query on every interface that can carry it, and hands `found` the records in the answers that arrive
 * within `windowMs`. Answers from beyond the local network are ignored.
 * @param {import('./dns.js').DnsQuestion[]} questions
 * @param {number} windowMs
 * @param {DiscoveredServices} found
 * @param {AbortSignal} [signal]
 */
async function query(questions, windowMs, found, signal) {
  // Checked first as well, so that a browse that was cancelled stops even where no interface can send a query.
  signal?.throwIfAborted();
  const id = randomInt(1, 65536);
  const message = encodeDns({ id, isResponse: false, authoritative: false, questions, answers: [], additionals: [] });
  const sockets = await openSockets(message, id, found);
  if (sockets.length === 0) {
    return;
  }
  const stop = new AbortController();
  const onAbort = () => stop.abort();
  signal?.addEventListener('abort', onAbort, { once: true });
  try {
    // Asked again a little later, like a browser that keeps watching, so that a receiver that has only just started
    // advertising is found too.
    const resend = (async () => {
      for (const wait of [250, 750]) {
        await delay(wait, undefined, { signal: stop.signal });
        for (const { socket, destination } of sockets) {
          socket.send(message, MDNS_PORT, destination, () => {});
        }
      }
    })().catch(() => {});
    await delay(windowMs, undefined, { signal: stop.signal }).catch(() => {});
    stop.abort();
    await resend;
  } finally {
    stop.abort();
    signal?.removeEventListener('abort', onAbort);
    for (const { socket } of sockets) {
      socket.close();
    }
  }
  signal?.throwIfAborted();
}

/**
 * Opens one socket per interface and address family, sends the query on each, and returns them with where they sent
 * it.
 * @param {Buffer} message
 * @param {number} id
 * @param {DiscoveredServices} found
 */
async function openSockets(message, id, found) {
  /** @type {Array<{ socket: import('node:dgram').Socket, destination: string }>} */
  const sockets = [];
  /**
   * @param {import('node:dgram').Socket} socket
   * @param {string} [scope] the interface an IPv6 socket sends on, which its answers come back on
   */
  const listen = (socket, scope) => {
    socket.on('message', (data, remote) => {
      if (remote.port !== MDNS_PORT || !isLocalAddress(remote.address)) {
        return;
      }
      const answer = decodeDns(data);
      if (answer === null || !answer.isResponse || answer.id !== id) {
        return;
      }
      const sender = scope && !remote.address.includes('%') ? `${remote.address}%${scope}` : remote.address;
      found.add([...answer.answers, ...answer.additionals], sender);
    });
    socket.on('error', () => {});
  };
  for (const [name, entries] of Object.entries(networkInterfaces())) {
    const usable = (entries ?? []).filter((entry) => !entry.internal);
    for (const entry of usable.filter((each) => each.family === 'IPv4')) {
      const socket = createSocket({ type: 'udp4' });
      listen(socket);
      try {
        await new Promise((resolve, reject) => {
          socket.once('error', reject);
          socket.bind({ address: entry.address, port: 0, exclusive: true }, () => {
            socket.off('error', reject);
            resolve(undefined);
          });
        });
        socket.setMulticastInterface(entry.address);
        socket.setMulticastTTL(255);
        socket.send(message, MDNS_PORT, MULTICAST_V4, () => {});
        sockets.push({ socket, destination: MULTICAST_V4 });
      } catch {
        socket.close();
      }
    }
    const linkLocal = usable.find((each) => each.family === 'IPv6' && /^fe[89ab]/i.test(each.address));
    if (linkLocal) {
      const scope = process.platform === 'win32' ? String(linkLocal.scopeid) : name;
      const socket = createSocket({ type: 'udp6' });
      listen(socket, scope);
      try {
        await new Promise((resolve, reject) => {
          socket.once('error', reject);
          socket.bind({ address: '::', port: 0, exclusive: true }, () => {
            socket.off('error', reject);
            resolve(undefined);
          });
        });
        socket.setMulticastInterface(`::%${scope}`);
        socket.setMulticastTTL(255);
        const destination = `${MULTICAST_V6}%${scope}`;
        socket.send(message, MDNS_PORT, destination, () => {});
        sockets.push({ socket, destination });
      } catch {
        socket.close();
      }
    }
  }
  return sockets;
}
