# Eve's Components notes

Logic World mod (C# server + client, `.jecs` component definitions). Read `README.md` for what each component does.

## Status of the `ram32` branch (not merged)
- Adds `ProgramRAM32` ("Eve's RAM (32-bit)"): `components/ProgramRAM32.jecs`, `src/server/Ram32Logic.cs`.
- It also touched existing files: `RamServer.cs` now works through the new `IRomHost` interface (implemented by `RamLogic`,
  `MicrocodeRomLogic`, `Ram32Logic`), and `EveRomOperation.cs` lists the new type for the editor.
- **Nothing on this branch has been built or run.** The session had no .NET and no Logic World. Build it and try it in game first.
  Things most likely to need a fix: compile errors, the peg positions in `ProgramRAM32.jecs`, saving and loading worlds.

## Design of Ram32Logic
- Inputs: 0-31 address bits 0-31, 32-63 write data bits 0-31, 64 write enable. Outputs: 0-31 data bits 0-31.
  Index `i` is bit `i`, LSB on the right (same side as the other blocks; only the *index order* of MicrocodeROM differs).
- Every access is a dword at a byte address, any alignment, little-endian. Write on the rising edge of write-enable.
- Backs the whole 4 GiB sparsely: 8192 pages of 512 KiB, allocated on first non-zero write.
- Persistence: editor source text if there is any (runtime writes are dropped, as in Eve's RAM), otherwise the magic
  `EVERAM32\n` followed by (uint32 page index, 512 KiB) for every page that is not all zero.
- The editor and `@file` load up to 1 GiB from address 0 (`IRomHost.MaxImageBytes`; Eve's RAM and the microcode ROM 64 KiB).

## Owner's plans (not started)
- Bit 31 of the address is a page select in the owner's CPU: low half RAM, high half hardware. The block needs no gating.
- Drop the editor for this block and load a binary file from a path in the saves instead, chosen from a dropdown if Eccs's UI
  library has one (unchecked). The server must read the file (clients cannot see server files). Open: where the files live,
  whether RAM should survive a world reload or restart from the file each time.

## Known weak spots
- `RamLogic`, `MicrocodeRomLogic` and `Ram32Logic` repeat the save/restore/persist code.
- Editor type checks name components in several places (`EveRomOperation`, `RamEditorWindow.UpdateTitle`).
- `RamLogic`/`MicrocodeRomLogic` detect editor text by looking for `0x` or `@` in the saved bytes; raw data could be misread.
- No tests. `RamParser` is pure logic and easy to unit test.
