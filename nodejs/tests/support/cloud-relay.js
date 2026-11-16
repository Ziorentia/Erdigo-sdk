// Helpers for the tests of the cloud relay example: HTTP calls, raw requests, and running the two programs.

import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { connect } from 'node:net';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const EXAMPLE = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'examples', 'cloud-relay');
export const RELAY = join(EXAMPLE, 'relay.js');
export const GATEWAY = join(EXAMPLE, 'gateway.js');

/** A new random token, as docs/cloud-relay.md suggests making one. */
export function token() {
  return randomBytes(32).toString('base64url');
}

/**
 * @typedef {{ status: number, headers: import('node:http').IncomingHttpHeaders, text: string, json: any }} Answer
 */

/**
 * One HTTP or HTTPS request. `body` is sent as it is if it is a Buffer or a string, and as JSON otherwise. `ca` is the
 * certificate to trust for HTTPS.
 * @param {string} url
 * @param {{ method?: string, token?: string | null, body?: unknown, type?: string | null, headers?: Record<string, string>, ca?: Buffer }} [options]
 * @returns {Promise<Answer>}
 */
export function call(url, { method = 'GET', token: bearer = null, body = undefined, type = 'application/json', headers = {}, ca = undefined } = {}) {
  return new Promise((resolve, reject) => {
    /** @type {Record<string, string | number>} */
    const sent = { ...headers };
    if (bearer !== null) {
      sent.Authorization = `Bearer ${bearer}`;
    }
    /** @type {Buffer | null} */
    let data = null;
    if (body !== undefined) {
      data = Buffer.isBuffer(body) ? body : Buffer.from(typeof body === 'string' ? body : JSON.stringify(body), 'utf8');
      if (type !== null) {
        sent['Content-Type'] = type;
      }
      sent['Content-Length'] = data.length;
    }
    const request = url.startsWith('https:') ? httpsRequest : httpRequest;
    const outgoing = request(url, { method, headers: sent, agent: false, ca }, (response) => {
      /** @type {Buffer[]} */
      const chunks = [];
      response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        let json = null;
        try {
          json = JSON.parse(text);
        } catch {
          // Left as null; the test says what it expected.
        }
        resolve({ status: response.statusCode ?? 0, headers: response.headers, text, json });
      });
      response.on('error', reject);
    });
    outgoing.on('error', reject);
    outgoing.end(data ?? undefined);
  });
}

/**
 * Sends raw bytes and returns what comes back before the connection closes or a second passes without more.
 * @param {number} port
 * @param {string | Buffer} bytes
 */
export function raw(port, bytes) {
  return new Promise((resolve, reject) => {
    const socket = connect(port, '127.0.0.1');
    /** @type {Buffer[]} */
    const chunks = [];
    let timer = setTimeout(() => socket.end(), 3000);
    socket.on('data', (chunk) => {
      chunks.push(chunk);
      clearTimeout(timer);
      timer = setTimeout(() => socket.end(), 1000);
    });
    socket.on('close', () => {
      clearTimeout(timer);
      resolve(Buffer.concat(chunks).toString('latin1'));
    });
    socket.on('error', (error) => {
      if (chunks.length > 0) {
        return;
      }
      reject(error);
    });
    socket.write(bytes);
  });
}

/**
 * Runs a program to the end, with a time limit, and returns what it printed and its exit status.
 * @param {string} program
 * @param {string[]} args
 * @param {{ env?: Record<string, string | undefined>, timeoutMs?: number }} [options]
 * @returns {Promise<{ code: number | null, stdout: string, stderr: string }>}
 */
export function run(program, args, { env = {}, timeoutMs = 120_000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [program, ...args], {
      env: { ...process.env, ERDIGO_RELAY_TOKEN: undefined, ERDIGO_RELAY_SOURCE_TOKENS: undefined, ERDIGO_RELAY_GATEWAY_TOKEN: undefined, ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => {
      stdout += chunk;
    });
    child.stderr.on('data', (chunk) => {
      stderr += chunk;
    });
    const timer = setTimeout(() => child.kill(), timeoutMs);
    child.on('error', reject);
    child.on('close', (code) => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });
  });
}

/**
 * Starts a program that keeps running, and waits for a line of its output to match.
 * @param {string} program
 * @param {string[]} args
 * @param {RegExp} ready
 * @param {Record<string, string | undefined>} [env]
 */
export async function startProgram(program, args, ready, env = {}) {
  const child = spawn(process.execPath, [program, ...args], {
    env: { ...process.env, ERDIGO_RELAY_TOKEN: undefined, ERDIGO_RELAY_SOURCE_TOKENS: undefined, ERDIGO_RELAY_GATEWAY_TOKEN: undefined, ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const state = { stdout: '', stderr: '' };
  child.stdout.on('data', (chunk) => {
    state.stdout += chunk;
  });
  child.stderr.on('data', (chunk) => {
    state.stderr += chunk;
  });
  const exited = new Promise((resolve) => child.on('close', (code, signal) => resolve({ code, signal })));
  const end = Date.now() + 20_000;
  for (;;) {
    const match = ready.exec(state.stdout);
    if (match) {
      return { child, match, state, exited };
    }
    if (child.exitCode !== null || Date.now() > end) {
      child.kill();
      throw new Error(`the program did not start:\n${state.stdout}\n${state.stderr}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}
