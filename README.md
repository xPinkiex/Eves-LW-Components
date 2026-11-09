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

### Eve's Network Port
A plain byte pipe between the circuit and one TCP connection: a network card for a CPU. It listens (a terminal or a web browser connects) or connects out. 16 bits each way: write a data byte with control bits (8 SEND, 9 CMD, 10 HANGUP) on WR's rising edge; read the received byte with status bits (8 READY, 9 CONN, 10 LSN, 11 ROOM, 12 HUP, 13-15 ECODE). On a CPU bus: two 16-bit registers, DATA at +0 and STATUS at +2. RD_DATA takes the byte, RD_STATUS clears the sticky HUP and ECODE. Commands: `listen PORT`, `listen ADDR:PORT`, `connect HOST:PORT`, `close`. Pins, timing, wiring to a CPU bus and examples: [`docs/network-port.html`](docs/network-port.html).

`tools/lwterm.py` is a terminal for it (one Python file, raw mode, Ctrl+] quits): `python3 tools/lwterm.py localhost:7000`.

### LW32 CPU (in progress, branch `cpu-block`)
The LW32 emulator as a block on the CPU's bus (the plan: `docs/cpu-block.md` in the
lw-compiler repo). Its core is a native library, `native/liblw32core.so` (Linux x64), built
from lw-compiler by `emu/build-native.sh`. So far only a loading test: at start the server log
says `lw32core loaded ... (version 1, 2 + 3 = 5)`, or why it didn't load.

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