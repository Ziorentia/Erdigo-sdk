# Test vectors

**Every key and code in this folder is for testing only.** In `integration-v1/`, each private key is the SHA-256
of a public label, `erdigo-integration-v1 test-only key: <label>`, so anyone can see it was never a real key.
`noise/` holds the published keys of the official vectors. Never generate vectors from real keys.

## `noise/cacophony-subset.json`

An unmodified subset of the official [cacophony](https://github.com/haskell-cryptography/cacophony) test vectors,
which their authors released into the public domain. Use them to check your Noise implementation:

| Protocol | Why it is here |
|---|---|
| `Noise_IK_25519_ChaChaPoly_SHA256` | Used for ordinary connections |
| `Noise_XXpsk3_25519_ChaChaPoly_SHA256` | Exercises the `XX` pattern with a pre-shared key |
| `Noise_NNpsk0_25519_ChaChaPoly_SHA256` | Exercises a pre-shared key mixed in before the first message |

`Noise_XXpsk0`, which pairing uses, has no official vector. It combines the two behaviours above, and
`integration-v1/handshake-pair.json` was checked against an independent implementation (below).

## `integration-v1/`

| File | Contents |
|---|---|
| `handshake-connect.json` | A full connection: the first message's counter, the handshake, `hello`, `welcome`, a request and its `ack`, byte for byte |
| `handshake-pair.json` | A full pairing: the code, the derived key, the QR text, the handshake and `pair_status`, byte for byte |
| `messages.json` | Requests a receiver must accept, and requests it must reject with a specific error code |

In the handshake files, `frames` is in the order sent. `noise_message` is the Noise message and `wire` is what
goes on the connection, with its two-byte length. To reproduce a frame, use the listed private keys, including
the ephemeral ones. Real traffic must never reuse an ephemeral key.

To generate them again after changing the protocol:

```bash
swift run erdigo-vectors spec/vectors/integration-v1
```

## `tools/crosscheck.py`

Checks the vectors using code that shares nothing with the Swift SDK: the Python `noiseprotocol` library for the
handshakes, and `jsonschema` for the messages. Use it in a virtual environment:

```bash
python3 -m venv .venv
.venv/bin/pip install -r spec/vectors/tools/requirements.txt
.venv/bin/python spec/vectors/tools/crosscheck.py
```
