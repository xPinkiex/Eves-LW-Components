#!/usr/bin/env python3
"""lwterm.py -- a terminal for Eve's Network Port (docs/network-port.html).

    python3 lwterm.py [HOST[:PORT]] [--retry] [--no-hello]

Connects (default localhost:7000), puts the terminal in raw mode and passes
bytes both ways: every key goes out as you press it, everything that comes
back is printed as it arrives. The circuit at the other end echoes and edits.
Ctrl+] quits, whatever the other end does.

On connecting it sends one line, "LWTERM 1\\n", so a program serving the port
can tell a terminal from a web browser (which starts with "GET /...").
--no-hello leaves it out. --retry connects again when the connection closes or
can't be made (the world was reloaded, the program restarted), every second.
Needs only Python 3; Linux, macOS and Windows.
"""
import os
import socket
import sys
import time

HELLO = b"LWTERM 1\n"
QUIT = 0x1D                      # Ctrl+]


def note(msg):
    sys.stderr.write("\r\n[" + msg + "]\r\n")
    sys.stderr.flush()


def connect(host, port, hello):
    s = socket.create_connection((host, port), timeout=5)
    s.settimeout(None)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    if hello:
        s.sendall(HELLO)
    return s


def run_posix(sock):
    """Returns True when the user quit, False when the connection closed."""
    import select
    import termios
    import tty
    fd = sys.stdin.fileno()
    saved = termios.tcgetattr(fd)
    try:
        tty.setraw(fd)
        while True:
            ready, _, _ = select.select([fd, sock], [], [])
            if fd in ready:
                data = os.read(fd, 1024)
                if not data or QUIT in data:
                    cut = data.split(bytes([QUIT]))[0] if data else b""
                    if cut:
                        sock.sendall(cut)
                    return True
                sock.sendall(data)
            if sock in ready:
                data = sock.recv(4096)
                if not data:
                    return False
                os.write(sys.stdout.fileno(), data)
    finally:
        termios.tcsetattr(fd, termios.TCSADRAIN, saved)


def run_windows(sock):
    import msvcrt
    import threading
    closed = threading.Event()

    def reader():
        try:
            while True:
                data = sock.recv(4096)
                if not data:
                    break
                sys.stdout.buffer.write(data)
                sys.stdout.flush()
        except OSError:
            pass
        closed.set()

    threading.Thread(target=reader, daemon=True).start()
    while not closed.is_set():
        if not msvcrt.kbhit():
            time.sleep(0.01)
            continue
        ch = msvcrt.getwch()
        if ch in ("\x00", "\xe0"):       # arrow and function keys: two codes, dropped
            msvcrt.getwch()
            continue
        if ord(ch) == QUIT:
            return True
        sock.sendall(ch.encode("utf-8", "replace"))
    return False


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    flags = {a for a in sys.argv[1:] if a.startswith("--")}
    if flags - {"--retry", "--no-hello"} or len(args) > 1:
        sys.exit(__doc__)
    target = args[0] if args else "localhost:7000"
    host, _, port = target.rpartition(":") if ":" in target else (target, "", "7000")
    port = int(port)
    retry = "--retry" in flags
    run = run_windows if os.name == "nt" else run_posix
    while True:
        try:
            sock = connect(host, port, "--no-hello" not in flags)
        except OSError as e:
            if not retry:
                sys.exit(f"lwterm: {host}:{port}: {e}")
            time.sleep(1)
            continue
        note(f"connected to {host}:{port}, Ctrl+] quits")
        try:
            quit_ = run(sock)
        except OSError:
            quit_ = False
        sock.close()
        if quit_:
            note("quit")
            return
        note("connection closed")
        if not retry:
            return
        time.sleep(1)


if __name__ == "__main__":
    main()
