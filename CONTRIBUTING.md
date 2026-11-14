# Contributing

Thank you for helping. A few rules keep this repository safe to use.

## Before you start

- **Security problems go through private reporting.** See [SECURITY.md](SECURITY.md).
- **Protocol changes start with the specification.** Change [spec/integration-v1.md](spec/integration-v1.md), the
  schema and the vectors together, because every SDK depends on them.

## Checks

```bash
swift build
swift test
swift/Tests/cli-smoke.sh
```

If you touch the wire format, also generate the vectors again and run the independent check. See
[spec/vectors/README.md](spec/vectors/README.md).

## Rules for the code

- **Do not write new cryptography.** Use the Noise patterns in the specification.
- **Do not add dependencies** without a strong reason. The Swift SDK has none today, and that is on purpose.
- **Test with made-up data.** Use keys from `spec/vectors` or generated in the test, addresses from
  `192.0.2.0/24`, and the coordinates of public places. Never commit real keys, fingerprints, pairing codes,
  coordinates, device names or addresses.
- **A pairing code never goes in a command-line argument or a log.**
- **Automation stays minimal.** Pin actions to a full commit hash, keep the token at `contents: read`, and do not add
  repository secrets or `pull_request_target`.

## Sign-off

Please sign off your commits to show you have the right to submit the work, following the
[Developer Certificate of Origin](https://developercertificate.org/):

```bash
git commit -s
```

Contributions are accepted under the [MIT license](LICENSE).
