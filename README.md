# Eve's Components

Custom components for Logic World.

**Documentation:** [`docs/index.html`](docs/index.html), a page per component with its pins drawn, how it behaves and how to use it (open the files in a browser; they need nothing else).

## Components

### Eve's RAM
16-bit addressable RAM (64KB) with byte-level read/write. Includes an in-game text editor for programming the ROM.

- 16 address input pegs, 8 data output pegs, 8 data input pegs, 1 write-enable peg
- Rising edge of write-enable latches data-in at the addressed byte
- Source text is persisted in the world save

### Eve's Microcode ROM
32-bit microcode ROM with address-based dword read/write. Includes an in-game text editor.

- 16 address input pegs, 32 data output pegs, 32 data input pegs, 1 write-enable peg
- Rising edge of write-enable latches data-in at the addressed dword

### Eve's Normally-Closed Relay
A relay that is closed by default and opens when its control input is high. Inverse of the standard relay.

- Peg 0: control input (high = open, low = closed)
- Pegs 1-2: the switched connection

### Eve's Breakpoint
Pauses the simulation when the input peg goes from low to high (rising edge). Useful for debugging CPU designs — wire it to a halt signal, overflow flag, or any condition you want to catch.

- 1 input peg (trigger signal)
- No outputs
- Edge-triggered: only pauses on low-to-high transition, won't re-pause while signal stays high
- Stays paused until manually resumed
- Pauses within **1 tick** of the rising edge at any simulation speed (see "Breakpoint pause timing" below)
- Logs "Breakpoint hit!" to the console when triggered

**To resume:** Click the pause/play button in the client UI, or run `simulation.resume` in the console. Single-stepping still works while paused.

#### Breakpoint pause timing
Logic World's simulation runs ticks in catch-up batches: each `Tick()` call runs as many `DoLogicUpdate()` calls as needed to catch up to real time, capped by a wall-clock budget. Vanilla only checks the pause flag once at the top of each `Tick()`, so a pause request issued *inside* a batch (by a component's `DoLogicUpdate`) isn't honored until the whole batch finishes — which means more missed ticks the higher your TPS. This mod ships a Harmony patch on `SimulationManager.Tick` that re-checks the running state after every `DoLogicUpdate()` and aborts the batch immediately when a pause is requested mid-batch, so the breakpoint pauses within one tick regardless of TPS.

### Eve's Serial Port
A byte stream between the circuit and a TCP connection, like a UART whose other end is a socket: a terminal on the PC can type into the CPU and read what it writes, and the CPU can connect out. The network I/O runs on a thread of the server; the pins only move bytes in and out of two 4096-byte queues.

- Inputs (back face): 0-7 TX data (bottom row, bit 0 on the right), 8 WR (rising edge: the TX byte is sent, or with CMD high added to the command line), 9 CMD, 10 RD (falling edge: the RX byte shown is consumed and the next one shown)
- Outputs (front face): 0-7 RX data, 8 RXV (a byte is shown), 9 CONN (connected), 10 LSN (listening), 11 TXR (room to send), 12 ERR (the last command failed)
- Commands, bytes written with CMD high and ended by `\n` or `\r`:
  - `listen PORT [telnet]`: accepts one connection at a time on 127.0.0.1 (`listen ADDR:PORT` for another address, `0.0.0.0` for every interface). With `telnet` the client is asked for character mode without local echo, telnet commands are dropped from the input and CR LF becomes CR, so `telnet localhost PORT` works as a terminal.
  - `connect HOST:PORT`: connects out (to reach a remote shell over SSH, run e.g. `socat TCP-LISTEN:7001,bind=127.0.0.1,fork EXEC:"ssh user@host",pty` on the PC and connect to 127.0.0.1:7001).
  - `close`
- From another machine, use SSH to the PC: `ssh -L 7000:localhost:7000 you@pc`, then `telnet localhost 7000`.
- The connection isn't saved with the world: after a reload the program sends its command again.

For LW32 (the LW-Compiler toolchain's `serial` device at 0x70000): a write to DATA (0x70000) strobes WR, address bit 2 drives CMD (so 0x70004 is the command register), a read of DATA strobes RD, and a read of either gives outputs 0-12 as bits 0-12.

## ROM Editor

Press **X** while looking at a RAM or Microcode ROM block to open the editor.

### Syntax

| Token | Description |
|---|---|
| `0xFF` | Hex byte(s), up to 8 digits (little-endian word semantics) |
| `0b11001000` | Binary byte(s), up to 16 bits |
| `@0xADDR` | Set current address (up to 4 hex digits) |
| `@file PATH` | Load a file at the current address (`@file "PATH"` if it has spaces) |
| `// comment` | Single-line comment (ignored) |
| `# comment` | Single-line comment (ignored) |

### Loading files (`@file`)

For programs too big to paste. Put the file in the `bins` folder of the save
(`Logic World/saves/<save name>/bins`; the compiler's `--save` creates it; absolute paths work too)
and write in the editor:

```
@file kernel.bin
@0x8000 @file data.bin      // more files or bytes at other addresses
```

- `.bin` files are loaded as raw bytes. Any other file is read as text in the editor's own
  format, so the Trapdoor assembler's `.hex` output loads as is. Included text can use `@0x`
  and `@file` itself.
- After a file, loading continues right after its last byte. Everything else in the editor
  still counts.
- The server reads the files, on **Apply** and again **every time the world loads**: the world
  save keeps only the editor text. After recompiling, press Apply (or reload the world). The
  editor shows the server's answer (byte count or error). If a file is missing when the world
  loads, the block starts empty and the server log says why; the editor text is kept.
- The save's `bins` folder travels with the save. If the server can't find the save (its log
  says so at start-up), files are read from `Logic World/roms` instead.
- The Trapdoor compiler writes `.bin` files: `clang-trapdoor.sh ... --save <save name>` puts the
  program straight into that save's `bins` folder.

### Buttons

- **Apply** — Parse and load the text into ROM
- **Clear** — Clear the ROM and editor text
- **Save** — Save current source text to the world file (drops runtime writes)
- **Restore** — Revert to the last saved state

## Dependencies

- [EccsLogicWorldAPI](https://github.com/Ecconia/Ecconia-LogicWorld-Mods)
- [HarmonyForLogicWorld](https://github.com/Ecconia/Ecconia-LogicWorld-Mods) (used by the Breakpoint's pause-timing patch)