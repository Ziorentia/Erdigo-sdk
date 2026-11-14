// Type declarations for erdigo-sdk/terminal: reading from the person at the keyboard, and only from a terminal.

/** True if both standard input and standard output are a terminal, so that a person can see a question and answer it. */
export function personIsThere(): boolean;

/**
 * Shows `prompt` on the terminal and reads one line from it, without showing what is typed. Anything typed before the
 * prompt is discarded. Resolves to null if there is no terminal, and to an empty string at the end of input.
 */
export function readSecretLine(prompt: string, maxLength: number): Promise<string | null>;

/**
 * Asks a question and reads the answer, showing what is typed. Only an answer given after the question has appeared
 * counts, so anything typed before it is discarded. The lines go to standard output, the last of them the question
 * itself. Resolves to null at the end of input or when there is no terminal.
 */
export function ask(lines: string[]): Promise<string | null>;

/** A mistake in how a tool was called. Its message never repeats what was typed. */
export class UsageError extends Error {}

/** The person pressed Ctrl-C at a question. */
export class Interrupted extends Error {}
