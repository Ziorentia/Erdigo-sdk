# Security policy

This repository holds the Erdigo integration protocol and its SDKs. Please report anything that lets someone
get around what the [threat model](spec/threat-model.md) promises.

## Reporting a problem

**Please do not open a public issue.** Use GitHub's private reporting instead: on this repository's
[Security tab](https://github.com/Ziorentia/Erdigo-sdk/security), choose **Report a vulnerability**.

Please remove real data from your report: keys, device or receiver fingerprints, pairing codes and QR codes,
coordinates, addresses and map screenshots. `--redact` on any `erdigo-send` command hides most of these from its output.

## What to expect

| Step | When |
|---|---|
| We confirm we received your report | Within 3 business days |
| We tell you how we classified it | Within 7 days |
| We agree with you when to publish | Within 90 days, or sooner once a fix is out |

We publish fixes and advisories through this repository's security advisories.

## What is in scope

- The protocol: [spec/integration-v1.md](spec/integration-v1.md)
- The SDKs and the `erdigo-send` tool in this repository

The mock receiver is a test tool and is not meant to face a hostile network. Weaknesses that follow from this are
already listed in the threat model, but tell us if it misstates what the protocol requires.

For the Erdigo app itself, use the same private reporting channel.

## If a flaw is found in the protocol

Section 15 of the specification describes how version 1 connections can be stopped, without depending on any server.
