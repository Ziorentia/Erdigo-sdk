# Erdigo-sdk

Official Erdigo SDK by Ziorentia LLC.

Put pins, markers, notes, photos and tracks on an Erdigo map from your own programs, such as a script on a PC, a
gateway that reads your sensors, or a ground station that relays where a drone is. They talk to the Erdigo app on the
same local network.

The SDKs carry what your program sends; they do not read sensors or drones themselves. Your program gets the readings
or positions from the device in its own way and sends them through the SDK. There is no ready-made integration for any
particular drone or board.

> **In store review.** Erdigo 2.17.23, the first version of the app that accepts these connections, is waiting for
> review on the Mac App Store and the Microsoft Store. Until it is released, develop and test against the mock
> receiver included with each SDK. This notice will be removed once the review is complete.

## Get the Erdigo app

The SDKs send to the Erdigo app, which must be running on a Mac or a Windows PC on the same local network. Install it
from the store for that computer:

| App | Store | Version |
|---|---|---|
| Erdigo for Mac | [Mac App Store](https://apps.apple.com/us/app/erdigo/id6446225102?mt=12) | 2.17.23 or later |
| Erdigo for Windows | [Microsoft Store](https://apps.microsoft.com/detail/9ndwqg2sdr7x?hl=en-US) | 2.17.23 or later |

Either SDK works with either app: a program using the Windows SDK can write to a Mac, and the other way round.

### System requirements for the app

These are for the Erdigo app that receives. The SDKs have their own, listed under [Platforms](#platforms).

**Erdigo for Mac**

| | Minimum | Recommended |
|---|---|---|
| macOS | macOS 12 Monterey | macOS 13 Ventura or later |
| Mac | Intel or Apple silicon | Apple silicon |
| Memory | 4 GB | 8 GB or more |
| Network | Wi-Fi or Ethernet, on the same local network as your devices | The same, with an internet connection for maps and map layers |

On macOS 12, camera and stream video plays only if it is H.264. H.265 (HEVC) video and using an iPhone as a camera
need macOS 13 or later.

**Erdigo for Windows**

| | Minimum | Recommended |
|---|---|---|
| Windows | Windows 10 version 1809 (build 17763) | Windows 11 |
| Processor | 1 GHz or faster: x64, x86 or ARM64 | 2 GHz dual-core or faster |
| Memory | 4 GB | 8 GB |
| Graphics | DirectX 9 | DirectX 11 |
| Network | Wi-Fi or Ethernet, on the same local network as your devices | The same, with an internet connection for maps and map layers |

H.265 (HEVC) camera video needs the HEVC Video Extensions from the Microsoft Store; the app offers to install them.
Without them it shows the camera's H.264 stream if the camera has one, and still records the original. WebRTC
streams use the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and is on most Windows 10 PCs.

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
| Windows 10 or later (.NET 8) | [`windows/`](windows/README.md) | Preview |
| Linux, macOS or Windows (Python 3.10 or later), for scripts and boards such as a Raspberry Pi | [`python/`](python/README.md) | Preview |
| Node.js 20 or later on macOS, Windows or Linux | [`nodejs/`](nodejs/README.md) | Preview |
| Java 17 or later (Windows, macOS, Linux) | [`java/`](java/README.md) | Preview |
| PHP 8.1 or later, on the local network: a NAS, a server or a script | [`php/`](php/README.md) | Preview |

**Linux, including a Raspberry Pi.** The .NET SDK also builds and runs on Linux, and its tests run on Linux (x64) in
CI. It is supported there for development and testing only, it has not been tried on a Raspberry Pi or another ARM
board, and the device key is kept in a file only you can read (mode 0600) rather than encrypted. For another language,
implement the [protocol specification](spec/integration-v1.md).

## Try it in two minutes

You need Xcode or the Swift command-line tools. In one terminal, start a mock receiver:

```bash
swift run erdigo-send serve --mock
```

It prints a port, a pairing code and the receiver's key. In another terminal, replace `PORT` with that port, type the
code when asked, and type `yes` if the key shown matches the one the mock printed:

```bash
swift run erdigo-send pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

Then send a pin:

```bash
swift run erdigo-send pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts.

On Windows, install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and do the same from the
`windows` folder with `dotnet run --project src/erdigo-send --` in place of `swift run erdigo-send`, for example
`dotnet run --project src/erdigo-send -- serve --mock`. The two SDKs and their mock receivers work with each other. See
the [Windows SDK guide](windows/README.md).

## Documentation

- [macOS SDK guide](macos/README.md)
- [Windows SDK guide](windows/README.md)
- [Protocol specification](spec/integration-v1.md), for building an SDK in another language or a receiver
- [Threat model](spec/threat-model.md): what this protects against, and what it does not
- [Test vectors](spec/vectors/README.md)
- [Reporting a security problem](SECURITY.md)
- [Contributing](CONTRIBUTING.md)

## Status of the security work

The cryptography follows the standard [Noise Protocol Framework](https://noiseprotocol.org/) and passes the official
test vectors. **It has not had an independent security audit.** The receiver is part of the Erdigo app from version
2.17.23.

## How this SDK was made

Both SDKs were written and verified with AI coding agents, Anthropic's [Claude Code](https://claude.com/claude-code) and
OpenAI's [Codex](https://openai.com/codex/), working for Ziorentia LLC. Besides the automated tests and the official
Noise test vectors, the agents checked each SDK against the real Erdigo app on its platform: pairing, sending items
and reconnecting.

## License

[MIT](LICENSE)
