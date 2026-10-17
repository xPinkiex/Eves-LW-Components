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
- Logs "Breakpoint hit!" to the console when triggered

**To resume:** Click the pause/play button in the client UI, or run `simulation.resume` in the console. Single-stepping still works while paused.

## ROM Editor

Press **X** while looking at a RAM or Microcode ROM block to open the editor.

### Syntax

| Token | Description |
|---|---|
| `0xFF` | Hex byte(s), up to 8 digits (little-endian word semantics) |
| `0b11001000` | Binary byte(s), up to 16 bits |
| `@0xADDR` | Set current address (up to 4 hex digits) |
| `// comment` | Single-line comment (ignored) |
| `# comment` | Single-line comment (ignored) |

### Buttons

- **Apply** — Parse and load the text into ROM
- **Clear** — Clear the ROM and editor text
- **Save** — Save current source text to the world file (drops runtime writes)
- **Restore** — Revert to the last saved state

## Dependencies

- [EccsLogicWorldAPI](https://github.com/Ecconia/EccsLogicWorldAPI)