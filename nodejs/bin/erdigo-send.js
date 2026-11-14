#!/usr/bin/env node
// Writes pins, markers, notes, photos and tracks to a paired Erdigo receiver. Run with `help` for every command.

import { main } from '../src/cli/erdigo-send.js';

process.exitCode = await main(process.argv.slice(2));
