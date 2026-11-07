#!/usr/bin/env python3
"""Runs a pairing command with a chosen kind of standard input, and reports what it did.

The command reaches the receiver through a small proxy that counts connections. An argument of the
command that is exactly PROXY_PORT is replaced by the proxy's port.

    typed-ahead-check.py RECEIVER_PORT MODE [--type-code CODE] -- COMMAND...

MODE is one of:

    --pipe TEXT
        No controlling terminal. Standard input is a pipe holding TEXT and then end of input.
    --terminal
        In a pseudo terminal. The command must finish without asking about the receiver's key.
    --answer ANSWER
        In a pseudo terminal. The command must ask about the key, and ANSWER is typed once it has.
    --early EARLY --answer ANSWER
        The same, except that the receiver's answer is held back, and EARLY is typed while the command
        waits for it. The key is not on the screen yet, so the command must then keep waiting at its
        question, and only ANSWER, typed after it, may count.

--type-code types CODE in the terminal when the command asks for a pairing code.

Exits with 0 if the command behaved as MODE says. It then prints the command's exit status, the number
of connections that reached the proxy, and the command's output, for the caller to check. Nothing here
can wait for long: a command still running after LIMIT_SECONDS is stopped, and that is a failure.
"""

import os
import pty
import select
import signal
import socket
import subprocess
import sys
import threading
import time

HOLD_SECONDS = 1.0
WAIT_AT_QUESTION_SECONDS = 1.0
# A connection made just before a command ended may not have been accepted yet.
SETTLE_SECONDS = 0.2
QUESTION = b"type yes"
LIMIT_SECONDS = 45


class RanTooLong(Exception):
    pass


def ran_too_long(*_):
    raise RanTooLong()


def fail(message, output=b""):
    print(f"{message}\n{output.decode(errors='replace')}")
    sys.exit(1)


class Proxy:
    """Passes everything on and counts connections. It can hold back the first answer of each one."""

    def __init__(self, receiver_port, hold_seconds):
        self.receiver_port = receiver_port
        self.hold_seconds = hold_seconds
        self.listener = socket.create_server(("127.0.0.1", 0))
        self.port = self.listener.getsockname()[1]
        self.device_is_waiting = threading.Event()
        self.connections = 0

    def start(self):
        threading.Thread(target=self.serve, daemon=True).start()

    def serve(self):
        while True:
            device, _ = self.listener.accept()
            self.connections += 1
            try:
                receiver = socket.create_connection(("127.0.0.1", self.receiver_port))
            except OSError:
                device.close()
                continue
            threading.Thread(target=self.to_receiver, args=(device, receiver), daemon=True).start()
            threading.Thread(target=self.to_device, args=(receiver, device), daemon=True).start()

    def to_receiver(self, device, receiver):
        self.pump(device, receiver, self.device_is_waiting.set)

    def to_device(self, receiver, device):
        self.pump(receiver, device, lambda: time.sleep(self.hold_seconds))

    @staticmethod
    def pump(source, destination, before_first_chunk):
        first = True
        try:
            while chunk := source.recv(65536):
                if first:
                    before_first_chunk()
                    first = False
                destination.sendall(chunk)
        except OSError:
            pass
        for side in (source, destination):
            try:
                side.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


class Terminal:
    def __init__(self, command):
        self.pid, self.fd = pty.fork()
        if self.pid == 0:
            try:
                os.execv(command[0], command)
            finally:
                os._exit(127)
        self.output = b""
        self.status = None
        self.closed = False

    def pump(self, seconds):
        end = time.monotonic() + seconds
        while True:
            remaining = end - time.monotonic()
            if remaining <= 0:
                return
            ready, _, _ = select.select([self.fd], [], [], remaining)
            if ready:
                try:
                    chunk = os.read(self.fd, 65536)
                except OSError:
                    chunk = b""
                if not chunk:
                    self.closed = True
                    return
                self.output += chunk

    def wait_for_output(self, text):
        while text not in self.output:
            if self.has_exited():
                return False
            self.pump(0.05)
        return True

    def has_exited(self):
        # A closed terminal means the command has ended, even if the system has not reported it yet.
        if self.status is None:
            pid, status = os.waitpid(self.pid, 0 if self.closed else os.WNOHANG)
            if pid:
                self.status = os.waitstatus_to_exitcode(status)
        return self.status is not None

    def wait_for_exit(self):
        while not self.has_exited():
            self.pump(0.05)
        self.pump(0.1)

    def type(self, text):
        os.write(self.fd, text)

    def stop(self):
        # The command may be a shell with a pipeline, so this stops its whole process group.
        if self.status is None:
            try:
                os.killpg(self.pid, signal.SIGKILL)
            except OSError:
                pass
            os.close(self.fd)
            os.waitpid(self.pid, 0)


def run_with_pipe(proxy, command, text):
    proxy.start()
    try:
        finished = subprocess.run(
            command, input=text, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True
        )
    except RanTooLong:
        fail("the command did not finish in time")
    time.sleep(SETTLE_SECONDS)
    return finished.returncode, finished.stdout


def run_in_terminal(proxy, command, settings):
    # Threads start only after the fork, which is not safe to do with threads running.
    terminal = Terminal(command)
    proxy.start()
    try:
        if "--type-code" in settings:
            if not terminal.wait_for_output(b"Pairing code:"):
                fail("the command did not ask for the code", terminal.output)
            terminal.type(settings["--type-code"])
            terminal.type(b"\n")
        if "--terminal" in settings:
            while not terminal.has_exited():
                if QUESTION in terminal.output:
                    fail("the command asked about the key", terminal.output)
                terminal.pump(0.05)
            terminal.pump(0.1)
        else:
            answer_the_question(terminal, proxy, settings)
    except RanTooLong:
        fail("the command did not finish in time", terminal.output)
    finally:
        terminal.stop()
    return terminal.status, terminal.output


def answer_the_question(terminal, proxy, settings):
    early = settings.get("--early")
    if early is not None:
        while not proxy.device_is_waiting.is_set():
            if terminal.has_exited():
                fail("the command did not contact the receiver", terminal.output)
            terminal.pump(0.02)
        if b"Receiver key" in terminal.output:
            fail("the key was already on the screen, so this checked nothing", terminal.output)
        terminal.type(early)

    if not terminal.wait_for_output(QUESTION):
        fail("the command stopped without asking about the key", terminal.output)

    if early is not None:
        # An answer ends the command at once if it is a no, and makes it connect again if it is a yes.
        terminal.pump(WAIT_AT_QUESTION_SECONDS)
        if terminal.has_exited() or proxy.connections > 1:
            fail("text typed before the key appeared answered the question", terminal.output)

    terminal.type(settings["--answer"])
    terminal.wait_for_exit()


def main():
    arguments = sys.argv[1:]
    separator = arguments.index("--")
    options, command = arguments[:separator], arguments[separator + 1:]
    receiver_port = int(options.pop(0))
    settings = {}
    while options:
        name = options.pop(0)
        settings[name] = True if name == "--terminal" else options.pop(0).encode()

    proxy = Proxy(receiver_port, HOLD_SECONDS if "--early" in settings else 0)
    command = [str(proxy.port) if part == "PROXY_PORT" else part for part in command]

    signal.signal(signal.SIGALRM, ran_too_long)
    signal.alarm(LIMIT_SECONDS)
    if "--pipe" in settings:
        status, output = run_with_pipe(proxy, command, settings["--pipe"])
    else:
        status, output = run_in_terminal(proxy, command, settings)
    print(f"exit status {status}")
    print(f"connections {proxy.connections}")
    print(output.decode(errors="replace"))


if __name__ == "__main__":
    main()
