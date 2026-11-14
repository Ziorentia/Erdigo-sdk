// Type declarations for the Erdigo SDK for Node.js. Written by hand; they describe src/index.js.

/// <reference types="node" />

/** The kinds of item a device can write. */
export type ItemKind = 'pin' | 'marker' | 'note' | 'photo' | 'track';

/** The colours a marker can have. */
export type MarkerColor = 'red' | 'orange' | 'yellow' | 'green' | 'blue' | 'purple' | 'gray';

/** The only photo formats the protocol allows. */
export type PhotoType = 'image/jpeg' | 'image/png';

/** A place on the map, in degrees (WGS 84): latitude within -90...90 and longitude within -180...180. */
export interface Coordinate {
  lat: number;
  lon: number;
}

/** One point of a track. `ts` is milliseconds since 1970 (0...2^53-1), and `alt` metres (-1000...100000). */
export interface TrackPoint extends Coordinate {
  ts?: number | null;
  alt?: number | null;
}

/** Limits a receiver enforces per device. The receiver announces them in `welcome`. */
export interface Limits {
  items: number;
  photos: number;
  photoBytes: number;
  noteBytes: number;
  titleScalars: number;
  trackPoints: number;
  openTracks: number;
  tracks: number;
  messagesPerSecond: number;
  messageBurst: number;
  pointsPerSecond: number;
  pointBurst: number;
  diskBytes: number;
}

/** What a receiver says when it refuses something. */
export interface ReceiverError {
  /** Kept as text: a device must treat a code it does not know as a permanent failure. */
  code: string;
  seq: number | null;
  lastSeq: number | null;
  retryAfterMs: number | null;
  supported: number[] | null;
  fatal: boolean;
}

export const VERSION: 1;
export const SDK_NAME: string;
export const ITEM_KINDS: readonly ItemKind[];
export const MARKER_COLORS: readonly MarkerColor[];
/** The values of spec section 11. */
export const LIMITS_V1: Readonly<Limits>;

/** The error codes of spec section 10, as they are spelled on the wire. */
export const ErrorCode: Readonly<{
  versionUnsupported: 'version_unsupported';
  invalidMessage: 'invalid_message';
  invalidId: 'invalid_id';
  invalidCoordinate: 'invalid_coordinate';
  invalidText: 'invalid_text';
  notPermitted: 'not_permitted';
  notFound: 'not_found';
  kindConflict: 'kind_conflict';
  invalidState: 'invalid_state';
  staleSeq: 'stale_seq';
  seqConflict: 'seq_conflict';
  limitItems: 'limit_items';
  limitPhotos: 'limit_photos';
  limitTracks: 'limit_tracks';
  limitDisk: 'limit_disk';
  tooLarge: 'too_large';
  photoInvalid: 'photo_invalid';
  rateLimited: 'rate_limited';
  tooManyErrors: 'too_many_errors';
  paused: 'paused';
  replaced: 'replaced';
  codeReused: 'code_reused';
  internalError: 'internal_error';
}>;

/** A value that breaks a rule of the protocol, with the error code a receiver would report for it. */
export class WireError extends Error {
  constructor(code: string, detail: string);
  readonly code: string;
  readonly detail: string;
}

/** What went wrong, for {@link ErdigoError}. */
export const ErdigoErrorKind: Readonly<{
  /**
   * No receiver answered. A receiver stays silent when it is off, asleep, on another network, when the pairing code
   * is wrong or expired, and when this device is not allowed. These cases cannot be told apart, by design.
   */
  noAnswer: 'noAnswer';
  /** The receiver's key does not match the fingerprint supplied for pairing. */
  fingerprintMismatch: 'fingerprintMismatch';
  /** The person did not confirm the receiver's key, so the receiver was not asked to pair. */
  keyNotConfirmed: 'keyNotConfirmed';
  /** The person at the receiver declined the request. */
  pairingDenied: 'pairingDenied';
  /** The person at the receiver did not answer in time. */
  pairingTimedOut: 'pairingTimedOut';
  /** The code was used twice. The receiver warned its user and removed the pairing made with it. */
  codeReused: 'codeReused';
  /** The receiver rejected a request. See {@link ErdigoError.receiverError}. */
  rejected: 'rejected';
  /** The item kind is not among those the person allowed for this device. See {@link ErdigoError.itemKind}. */
  notPermitted: 'notPermitted';
  /** The receiver does not support any protocol version this SDK speaks. See {@link ErdigoError.supported}. */
  versionUnsupported: 'versionUnsupported';
  /** Another connection using this device key took over. */
  replaced: 'replaced';
  /** Another writer using this device key advanced the sequence number. See {@link ErdigoError.lastSeq}. */
  sequenceConflict: 'sequenceConflict';
  /** The receiver broke the protocol. */
  protocolViolation: 'protocolViolation';
  invalidArgument: 'invalidArgument';
  /** Stored state could not be used. See {@link ErdigoError.storageProblem}. */
  storage: 'storage';
}>;
export type ErdigoErrorKind = (typeof ErdigoErrorKind)[keyof typeof ErdigoErrorKind];

/** What was wrong with stored state. */
export const StorageProblem: Readonly<{
  none: 'none';
  /** The device key file exists but its contents are not a device key. A new key was not created. */
  identityDamaged: 'identityDamaged';
  /** The device key file exists but could not be opened or read. A new key was not created. */
  identityUnreadable: 'identityUnreadable';
  /** The pairing file exists but its contents could not be understood. */
  stateDamaged: 'stateDamaged';
  /** The pairing file exists but could not be opened or read. */
  stateUnreadable: 'stateUnreadable';
  /** A folder or file could be read or replaced by someone else, so it was not used. */
  insecure: 'insecure';
  /** Anything else, such as a full disk. */
  other: 'other';
}>;
export type StorageProblem = (typeof StorageProblem)[keyof typeof StorageProblem];

/** Failures reported by the SDK. */
export class ErdigoError extends Error {
  constructor(
    kind: ErdigoErrorKind,
    options?: {
      detail?: string | null;
      receiverError?: ReceiverError | null;
      itemKind?: string | null;
      supported?: number[] | null;
      lastSeq?: number | null;
      storageProblem?: StorageProblem;
      cause?: unknown;
    },
  );
  readonly kind: ErdigoErrorKind;
  readonly detail: string | null;
  readonly receiverError: ReceiverError | null;
  readonly itemKind: string | null;
  readonly supported: number[] | null;
  readonly lastSeq: number | null;
  readonly storageProblem: StorageProblem;
}

/**
 * The device's one secret: an X25519 key used only for this protocol. Printing or serialising an identity never shows
 * the key.
 */
export class DeviceIdentity {
  private constructor();
  /** The public key, which identifies this device to a receiver. */
  readonly publicKey: Buffer;
  /** 64 lowercase hexadecimal digits. */
  readonly fingerprint: string;
  static generate(): DeviceIdentity;
  /** Restores a key read from protected storage. The caller should clear `raw` afterwards. */
  static fromPrivateKey(raw: Uint8Array): DeviceIdentity;
  /** Only for writing the key to protected storage. The caller owns the copy and must clear it. */
  exportPrivateKeyForStorage(): Buffer;
  /** Clears the private key. The identity cannot be used afterwards. */
  dispose(): void;
}

/** What a device remembers about one receiver. An SDK can be paired with several receivers. */
export interface ReceiverPairing {
  /** A local name for this receiver. It is never sent anywhere. */
  label: string;
  receiverKey: Buffer;
  kinds: ItemKind[];
  lastHost: string | null;
  lastPort: number | null;
  pairedAt: Date;
  /**
   * The highest connect counter the receiver had accepted when it approved this pairing. A later counter must be
   * greater, even if this device has lost its own count.
   */
  counterFloor: number;
}

export function receiverPairing(
  fields: Pick<ReceiverPairing, 'label' | 'receiverKey' | 'kinds'> & Partial<ReceiverPairing>,
): ReceiverPairing;

/** The fingerprint of the receiver a pairing is for. */
export function pairingFingerprint(pairing: ReceiverPairing): string;

/** The one-time code a person reads from the receiver: 100 random bits as 20 Crockford base32 characters. */
export class PairingCode {
  /**
   * Reads a code as a person may type it: hyphens and white space are ignored, letters are uppercased, O is read as
   * 0, and I and L as 1. Anything else, or any length but 20, throws a {@link WireError}.
   */
  constructor(text: string);
  /** The 20 characters without separators. */
  readonly canonical: string;
  /** `XXXXX-XXXXX-XXXXX-XXXXX`, for showing on the receiver's screen. */
  readonly display: string;
  static generate(): PairingCode;
  /** The Noise pre-shared key derived from the code. The caller owns the result and should clear it. */
  presharedKey(): Buffer;
  equals(other: PairingCode): boolean;
}

/**
 * What a person gives the SDK to pair: the code, and the receiver's key fingerprint when it comes from the QR code.
 * A typed code has no fingerprint, and the person then has to compare the key.
 */
export class PairingTicket {
  constructor(code: PairingCode, fingerprint?: string | null);
  readonly code: PairingCode;
  readonly fingerprint: string | null;
  /** The text of the QR code, or null without a valid fingerprint. */
  readonly uri: string | null;
  /**
   * Accepts either a bare code or the QR form `erdigo-pair:1?code=...&fp=...`, which needs exactly one code and
   * exactly one valid fingerprint and nothing else. Throws a {@link WireError} for anything else.
   */
  static parse(text: string): PairingTicket;
}

/** Where to look for a receiver. */
export type ReceiverTarget = { readonly kind: 'discover' } | { readonly kind: 'address'; readonly host: string; readonly port: number };

export const ReceiverTarget: Readonly<{
  /**
   * Try the remembered address, then up to eight receivers advertised on the local network. An advertisement proves
   * nothing about who is behind it, so use {@link address} where that matters.
   */
  discover: ReceiverTarget;
  /** Try this address only. */
  address(host: string, port: number): ReceiverTarget;
}>;

export interface ReceiverEndpoint {
  host: string;
  port: number;
}

/** Listens for receivers on the local network for a short time, and returns up to `limit` of them. */
export function browse(options?: { durationMs?: number; limit?: number; signal?: AbortSignal }): Promise<ReceiverEndpoint[]>;

/** Persistent device state: its key, its pairings and the per-receiver connect counter. */
export interface DeviceStore {
  loadOrCreateIdentity(): DeviceIdentity;
  pairings(): ReceiverPairing[];
  /** Adds a pairing, or replaces the one for the same receiver. Its counter floor takes effect here. */
  save(pairing: ReceiverPairing): void;
  /** Remembers where a receiver was found. Does nothing if the pairing has since been forgotten. */
  updateAddress(receiverFingerprint: string, host: string, port: number): void;
  removePairing(receiverFingerprint: string): void;
  /**
   * Returns a value strictly greater than any previous result for this receiver. The value must be durable before it
   * is returned, because a receiver ignores a counter it has seen.
   */
  nextConnectCounter(receiverFingerprint: string, nowMs: number): number;
}

/** Keeps everything in memory. Intended for tests and short-lived programs. */
export class InMemoryDeviceStore implements DeviceStore {
  constructor(identity?: DeviceIdentity | null);
  loadOrCreateIdentity(): DeviceIdentity;
  pairings(): ReceiverPairing[];
  save(pairing: ReceiverPairing): void;
  updateAddress(receiverFingerprint: string, host: string, port: number): void;
  removePairing(receiverFingerprint: string): void;
  nextConnectCounter(receiverFingerprint: string, nowMs: number): number;
}

/**
 * Stores state in a private folder: `identity.key` and `pairings.json`. On macOS and Linux it refuses files that other
 * users could read or replace. A key file that exists but cannot be read is reported, and never replaced by a new key.
 */
export class FileDeviceStore implements DeviceStore {
  constructor(directory: string);
  readonly directory: string;
  /** The default folder for a profile, which no other edition of the SDK uses. */
  static defaultDirectory(profile?: string): string;
  loadOrCreateIdentity(): DeviceIdentity;
  pairings(): ReceiverPairing[];
  save(pairing: ReceiverPairing): void;
  updateAddress(receiverFingerprint: string, host: string, port: number): void;
  removePairing(receiverFingerprint: string): void;
  nextConnectCounter(receiverFingerprint: string, nowMs: number): number;
}

/**
 * Shows a person the receiver's key, and returns whether it matches the key on the receiver's screen. Only `true`
 * counts as a yes.
 * @param receiverKey the key in the form the receiver shows it: the first 16 digits of its fingerprint
 */
export type ConfirmReceiverKey = (receiverKey: string, context: { signal?: AbortSignal }) => boolean | Promise<boolean>;

export interface PairOptions {
  /** Where to find the receiver. */
  target: ReceiverTarget;
  /** The code, and the receiver's key fingerprint when it comes from the QR code. */
  ticket: PairingTicket;
  /** This device's key. */
  identity: DeviceIdentity;
  /** Shown to the person who confirms the request. Treated as untrusted by the receiver. */
  deviceName: string;
  /** What this device wants to send. The person may allow fewer. */
  kinds: Iterable<ItemKind>;
  /** A local name for the receiver. Defaults to the start of its fingerprint. */
  label?: string;
  /** How long a handshake may take. Five seconds by default. */
  handshakeTimeoutMs?: number;
  /** How long to wait for the person to confirm. 120 seconds by default. */
  approvalTimeoutMs?: number;
  /**
   * Needed for a typed code, because a code proves only that the other side knows it, and not called for any other
   * ticket. It runs before the receiver hears anything about this device.
   */
  confirmKey?: ConfirmReceiverKey;
  /** Stops pairing. A stopped pairing rejects with the signal's reason. */
  signal?: AbortSignal;
}

/** Pairs with a receiver and returns what to store about it. */
export function pair(options: PairOptions): Promise<ReceiverPairing>;

/**
 * How long a request keeps trying while the receiver cannot be reached. The time covers one outage: it counts from
 * when the connection was lost, and starts again once a connection is made. To limit a request's whole time, pass a
 * `signal`, such as `AbortSignal.timeout(120_000)`.
 */
export type RetryPolicy = { readonly kind: 'none' } | { readonly kind: 'forever' } | { readonly kind: 'upTo'; readonly ms: number };

export const RetryPolicy: Readonly<{
  /** Fail at the first connection problem. */
  none: RetryPolicy;
  /** Keep trying until the request is stopped. */
  forever: RetryPolicy;
  /** Keep trying for this long, counted from when the connection was lost. */
  upTo(ms: number): RetryPolicy;
}>;

export interface ClientOptions {
  /** This device's key. */
  identity: DeviceIdentity;
  /** The receiver to write to. */
  pairing: ReceiverPairing;
  /** Where the connection counter and the receiver's last address are kept. */
  store: DeviceStore;
  /** Where to look for the receiver. By default the last known address, then the local network. */
  target?: ReceiverTarget;
  /** How long each outage may last before a request fails with `noAnswer`. By default 30 seconds. */
  retry?: RetryPolicy;
  /** How long one attempt to connect may take. By default 5 seconds. */
  connectTimeoutMs?: number;
  /** How long to wait for the answer to one message. By default 15 seconds. */
  ackTimeoutMs?: number;
}

export interface RequestOptions {
  signal?: AbortSignal;
}

/**
 * Writes items to one paired receiver. Requests run strictly one at a time, in the order they were made, and each
 * resolves once the receiver has acknowledged it. If the connection breaks, a request is sent again with the same
 * sequence number, so the receiver applies it at most once.
 */
export class ErdigoClient {
  constructor(options: ClientOptions);
  /** The limits the receiver announced on the current connection. */
  readonly limits: Limits | null;
  /** The item kinds the person allowed, as announced on the current connection. */
  readonly allowedKinds: ItemKind[] | null;
  /** Connects now rather than at the first request. */
  connect(options?: RequestOptions): Promise<void>;
  /** Closes the connection. A later request connects again. */
  close(): void;
  ping(options?: RequestOptions): Promise<void>;
  /** Creates the pin, or replaces it if this device already has one with this id. */
  putPin(id: string, at: Coordinate, options?: RequestOptions & { title?: string | null }): Promise<void>;
  putMarker(id: string, at: Coordinate, options?: RequestOptions & { title?: string | null; color?: MarkerColor | null }): Promise<void>;
  /** A note is plain text. The receiver shows links as text and never opens them. */
  putNote(id: string, at: Coordinate, text: string, options?: RequestOptions): Promise<void>;
  /** Moves a pin, marker, note or photo. Tracks cannot be moved. */
  move(id: string, to: Coordinate, options?: RequestOptions): Promise<void>;
  /** Deletes one of this device's items. Deleting an id that does not exist succeeds. */
  delete(id: string, options?: RequestOptions): Promise<void>;
  /** Deletes every item this device created on this receiver, and nothing else. */
  deleteAll(options?: RequestOptions): Promise<void>;
  /** Appends points to a track, creating it if needed. Long lists go in batches of 100, with nothing in between. */
  appendTrack(id: string, points: TrackPoint[], options?: RequestOptions & { title?: string | null }): Promise<void>;
  finishTrack(id: string, options?: RequestOptions): Promise<void>;
  /** Sends a JPEG or PNG photo of at most 1048576 bytes. The format is detected from the data. */
  putPhoto(id: string, at: Coordinate, data: Uint8Array, options?: RequestOptions & { title?: string | null }): Promise<void>;
}

/** The receiver's or a device's key fingerprint, as 64 lowercase hexadecimal digits. */
export function fingerprint(publicKey: Uint8Array): string;
/** The first 16 hexadecimal digits in groups of four, for showing to people. */
export function shortFingerprint(fingerprint: string): string;
/** True for exactly 64 lowercase hexadecimal digits. */
export function isFingerprint(text: unknown): text is string;

/** Detects the format from the first bytes, or null for anything but JPEG or PNG. */
export function detectPhotoType(data: Uint8Array): PhotoType | null;
/** Removes unsafe characters and cuts the name to 40 characters, as a receiver does. */
export function sanitizeDeviceName(text: string): string;
/** These throw a {@link WireError} for a value that breaks the protocol's rules. */
export function validateCoordinate(value: unknown): Coordinate;
export function validateItemId(value: unknown): string;
export function validateTitle(text: unknown, limits?: Limits): void;
export function validateNote(text: unknown, limits?: Limits): void;
export function validateTrackPoint(value: unknown): TrackPoint;
