# Contributing

Thank you for helping. A few rules keep this repository safe to use.

## Before you start

- **Security problems go through private reporting.** See [SECURITY.md](SECURITY.md).
- **Protocol changes start with the specification.** Change [spec/integration-v1.md](spec/integration-v1.md), the
  schema and the vectors together, because every SDK depends on them.

## Questions, ideas and discussions

Use [Discussions](https://github.com/Ziorentia/Erdigo-sdk/discussions) for anything that is not a bug report:

| Category | For |
|---|---|
| Q&A | How to use an SDK or `erdigo-send`, pairing, and errors you do not understand |
| Ideas | New features, new languages and changes to the protocol. A protocol change is discussed here before the specification changes. |
| Show and tell | Something you built: a sensor, a drone, a script, a home automation |
| General | Anything else about the SDKs |
| Polls | Votes on what to do next |
| Announcements | Releases and changes, posted by the maintainers |

Elsewhere:

- A bug you can reproduce: open an [issue](https://github.com/Ziorentia/Erdigo-sdk/issues/new/choose).
- A security problem: never in public. Report it privately, as [SECURITY.md](SECURITY.md) says.
- A question about the Erdigo app itself rather than the SDKs: see the
  [Erdigo website](https://www.ziorentia.com/product.php?id=erdigo).

### Discussion rules

1. **Follow the [code of conduct](CODE_OF_CONDUCT.md).**
2. **Keep your data private.** This repository is public, and a post cannot be fully erased. Never post pairing codes,
   QR codes or QR text, keys, fingerprints, the files of a state folder such as `identity.json` and `pairings.json`,
   real coordinates, addresses or map screenshots. Add `--redact` to an `erdigo-send` command before you copy its
   output, and use made-up data in examples, as [Rules for the code](#rules-for-the-code) describes.
3. **Search first.** Your question may already have an answer in Discussions, the issues or the SDK guides.
4. **One topic per discussion,** with a title that says what it is about.
5. **Say what you use:** the SDK folder (swift, dotnet, python, nodejs, java or php), its version (or the commit, if you
   use a clone), the system with its runtime version, and the Erdigo app version if the app is involved. Show the smallest code and output that shows
   the problem.
6. **Write in English if you can,** so that everyone can search and follow. If that is hard, write in your own language
   and add a machine translation.
7. **AI tools are welcome, and you are responsible for what you post.** Check that the code you share runs.
8. **Be patient.** A small team looks after this repository, and answers come when someone has time. Please do not bump
   a discussion or mention the maintainers again and again.
9. **Close the loop.** In Q&A, mark the answer that solved your question, so the next person finds it.

Maintainers may edit out private data, move a discussion to another category, turn it into an issue, or lock or remove
posts that break these rules.

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
  repository secrets or `pull_request_target`. The one exception is the Publish workflow's publishing jobs, which also
  get `id-token: write` for trusted publishing and run only in the `pypi`, `npm` and `nuget` environments
  ([Releasing](docs/releasing.md)).

## Sign-off

Please sign off your commits to show you have the right to submit the work, following the
[Developer Certificate of Origin](https://developercertificate.org/):

```bash
git commit -s
```

Contributions are accepted under the [MIT license](LICENSE).
