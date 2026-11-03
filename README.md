# Eve's Components

Custom components for Logic World.

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

For programs too big to paste. Put the file in the `roms` folder in the Logic World folder
(created on first start; absolute paths work too) and write in the editor:

```
@file kernel.hex
@0x8000 @file data.bin      // more files or bytes at other addresses
```

- `.bin` files are loaded as raw bytes. Any other file is read as text in the editor's own
  format, so the Trapdoor assembler's `.hex` output loads as is. Included text can use `@0x`
  and `@file` itself.
- After a file, loading continues right after its last byte. Everything else in the editor
  still counts.
- The file is read on **Apply** and again **every time the world loads**: the world save keeps
  only the editor text. After recompiling, press Apply (or reload the world). If the file is
  missing when the world loads, the block starts empty and the server log says why; the
  editor text is kept.
- On a multiplayer server the file has to be in the server's `roms` folder too.

### Buttons

- **Apply** — Parse and load the text into ROM
- **Clear** — Clear the ROM and editor text
- **Save** — Save current source text to the world file (drops runtime writes)
- **Restore** — Revert to the last saved state

## Dependencies

- [EccsLogicWorldAPI](https://github.com/Ecconia/Ecconia-LogicWorld-Mods)
- [HarmonyForLogicWorld](https://github.com/Ecconia/Ecconia-LogicWorld-Mods) (used by the Breakpoint's pause-timing patch)