#!/usr/bin/env node
// The cloud relay: holds operations that devices send until the gateway fetches them. See docs/cloud-relay.md, or
// run it with --help.

import { main } from './lib/relay.js';

process.exitCode = await main(process.argv.slice(2), process.env);
