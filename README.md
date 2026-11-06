# Erdigo-sdk

Official Erdigo SDK by Ziorentia LLC.

Put pins, markers, notes, photos and tracks on an Erdigo map from your own devices and programs: a sensor, a drone, a
Raspberry Pi, a script. They talk to the Erdigo app on the same local network.

> **Preview.** The Erdigo app in release does not yet accept these connections. Until it does, develop and test
> against the mock receiver included with each SDK. The wire format may still change.

## How it works

- **Write only.** A device can add, move and delete its own items. It cannot read the map, anyone's location or
  anything else.
- **Local network only.** There is no internet path.
- **Permission first.** A person types a one-time code from the app into the device, then confirms the request in
  the app.
- **Silent to strangers.** The app answers nothing until a device proves it is allowed. Someone scanning the network
  gets no key and no reply, only the fact that a receiver is there. See the [threat model](spec/threat-model.md) for the limits.

## Platforms

| Platform | Where | Status |
|---|---|---|
| macOS 12 or later (Swift) | [`macos/`](macos/README.md) | Preview |
| Windows | | In progress |

## Try it in two minutes

You need Xcode or the Swift command-line tools. In one terminal, start a mock receiver:

```bash
swift run erdigo-send serve --mock
```

It prints a port and a pairing code. In another terminal, replace `PORT` with that port, and type the code when asked:

```bash
swift run erdigo-send pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

Then send a pin:

```bash
swift run erdigo-send pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts.

## Documentation

- [macOS SDK guide](macos/README.md)
- [Protocol specification](spec/integration-v1.md), for building an SDK in another language or a receiver
- [Threat model](spec/threat-model.md): what this protects against, and what it does not
- [Test vectors](spec/vectors/README.md)
- [Reporting a security problem](SECURITY.md)
- [Contributing](CONTRIBUTING.md)

## Status of the security work

The cryptography follows the standard [Noise Protocol Framework](https://noiseprotocol.org/) and passes the official
test vectors. **It has not had an independent security audit**, and it has not yet been tested against a real
receiver in the Erdigo app.

## License

[MIT](LICENSE)
