// The Erdigo SDK for Node.js. It writes pins, markers, notes, photos and tracks to an Erdigo receiver on the local
// network, over the integration-v1 protocol (spec/integration-v1.md). It runs in Node.js and similar server runtimes,
// not in a web browser, which cannot open TCP connections to the local network.

export { ErdigoClient, RetryPolicy } from './client.js';
export { ReceiverTarget, browse } from './discovery.js';
export { DeviceIdentity, ErdigoError, ErdigoErrorKind, StorageProblem, pairingFingerprint, receiverPairing } from './identity.js';
export {
  ITEM_KINDS,
  LIMITS_V1,
  MARKER_COLORS,
  PairingCode,
  PairingTicket,
  detectPhotoType,
  sanitizeDeviceName,
  validateCoordinate,
  validateItemId,
  validateNote,
  validateTitle,
  validateTrackPoint,
} from './model.js';
export { SDK_NAME, pair } from './pairing.js';
export { ErrorCode, VERSION, WireError, fingerprint, isFingerprint, shortFingerprint } from './protocol.js';
export { FileDeviceStore, InMemoryDeviceStore } from './store.js';
