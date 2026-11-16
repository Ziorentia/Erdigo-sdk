#!/usr/bin/env node
// The cloud gateway: fetches operations from a relay and writes them to the Erdigo app on this local network, as a
// paired device. See docs/cloud-relay.md, or run it with help.

import { main } from './lib/gateway.js';

process.exitCode = await main(process.argv.slice(2), process.env);
