using System;
using System.IO;
using System.Text;

// ROM Input Format (for compiler/emitter reference):
//
// The parser accepts a flat text format designed for pasting compiled
// machine code into the RAM editor window.  Each run of
// whitespace-separated tokens is written sequentially into RAM starting
// at the current address (0 by default).
//
// Supported token forms:
//
//   0xFF            hex byte(s)
//   0XFF            (case-insensitive prefix)
//   0b11001000      binary byte(s) (up to 16 bits → 2 bytes)
//   @0xADDR         set current address (4 hex digits max, 0–65535)
//   @file PATH      load a file at the current address (see below)
//   // comment      single-line comment (ignored until newline)
//   #  comment      single-line comment (ignored until newline)
//
// @file PATH (or @file "PATH WITH SPACES"):
//
//   A file ending in .bin is copied in as raw bytes. Any other file is read
//   as text in this same format, so the assembler's .hex output works as is
//   (its 0xNNNNNNNN words and // comments). Included text may itself use @0x
//   and @file (up to 8 levels deep). Afterwards the current address is the
//   address after the included data, and everything else in the editor still
//   counts, so a kernel and a data blob can be combined:
//
//     @file kernel.hex
//     @0x8000 @file data.bin
//
//   Files are read by the server only: relative paths are resolved against
//   the "bins" folder in the current save's folder (saves/<save>/bins), or
//   against "roms" in the Logic World folder if the server can't find the
//   save. Absolute paths work too. The editor sends text with @file to the
//   server unparsed and shows the server's answer. The file is read again on
//   every Apply and every time the world loads (the world save keeps only
//   the editor text), so after recompiling, press Apply or reload the world.
//
// The data length is the end of the highest byte written (or the final
// current address, if that is higher), so the order of @0x blocks doesn't
// matter.
// Multi-byte hex tokens — little-endian word semantics:
//
//   A 0x token may contain up to 8 hex digits.  The digit string is
//   treated as a sequence of 16-bit little-endian words and emitted
//   low-byte-first into memory:
//
//     0x00FF        → bytes [FF, 00]   (word 0x00FF, LE)
//     0xFF00        → bytes [00, FF]   (word 0xFF00, LE)
//     0xDEADBEEF    → bytes [EF, BE, AD, DE]
//     0xFF          → bytes [FF]       (single byte, same as before)
//     0xFFF         → bytes [FF, 0F]   (odd digits → left-padded to even)
//
//   This means the compiler can emit each 16-bit instruction word as a
//   single 0xNNNN token and it will be stored correctly in the
//   little-endian address space.
//
// Binary tokens use the same little-endian word rule for >8 bits, but
// are capped at 16 bits (2 bytes).  0b11001000 → [C8, 00].
//
// The editor window pre-fills its text field from the current ROM
// contents as one 0xNN token per byte (big-endian hex per byte, which
// round-trips through the parser identically).

namespace EVE.Components.Shared
{
    public static class RamParser
    {
        public struct ParseResult
        {
            public bool Success;
            public byte[] Data;
            public string Error;
            public int ByteCount;
        }

        public static bool TryDecodeUtf8(byte[] data, out string text)
        {
            text = null;
            if (data == null || data.Length == 0)
            {
                return false;
            }

            try
            {
                string decoded = Encoding.UTF8.GetString(data);
                if (Encoding.UTF8.GetByteCount(decoded) != data.Length)
                {
                    return false;
                }

                bool has0x = false;
                bool hasAt = false;
                for (int i = 0; i < decoded.Length; i++)
                {
                    char c = decoded[i];
                    if (c == '0' && i + 1 < decoded.Length && (decoded[i + 1] == 'x' || decoded[i + 1] == 'X'))
                    {
                        has0x = true;
                    }
                    else if (c == '@')
                    {
                        hasAt = true;
                    }
                }

                if (!has0x && !hasAt)
                {
                    return false;
                }

                text = decoded;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Fallback folder for relative @file paths (Logic World/roms, see InitFileRoot).</summary>
        public static string FileRoot;

        /// <summary>
        /// Preferred folder for relative @file paths, asked on every use: the server sets it to the
        /// current save's "bins" folder. Returning null falls back to FileRoot.
        /// </summary>
        public static Func<string> FileRootProvider;

        private const int MaxIncludeDepth = 8;

        /// <summary>
        /// Sets FileRoot (the fallback) to the "roms" folder in the Logic World folder, two levels
        /// above the mod's own folder (GameData/EVE_Components). Doesn't create it.
        /// </summary>
        public static void InitFileRoot(string modPath)
        {
            try
            {
                FileRoot = Path.GetFullPath(Path.Combine(modPath, "..", "..", "roms"));
            }
            catch
            {
                // Leave whatever was resolved; a missing folder just shows up as "not found".
            }
        }

        /// <summary>True if the text contains an @file directive (outside comments is not checked).</summary>
        public static bool HasFileDirective(string text)
        {
            return text != null && text.IndexOf("@file", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static ParseResult Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ParseResult { Success = true, Data = new byte[0], ByteCount = 0 };
            }

            var rom = new byte[65536];
            int currentAddress = 0;
            int end = 0;
            string error = ParseInto(text, rom, ref currentAddress, ref end, 0, null);
            if (error != null)
            {
                return new ParseResult { Success = false, Error = error };
            }

            int actualLength = Math.Max(currentAddress, end);
            byte[] result = new byte[actualLength];
            Array.Copy(rom, result, actualLength);

            return new ParseResult
            {
                Success = true,
                Data = result,
                ByteCount = actualLength,
            };
        }

        /// <summary>Resolves an @file path: absolute as is, relative against FileRoot.</summary>
        public static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(CurrentRoot() ?? ".", path);
        }

        /// <summary>The folder relative @file paths resolve against right now.</summary>
        public static string CurrentRoot()
        {
            string root = null;
            try { root = FileRootProvider?.Invoke(); } catch { }
            return root ?? FileRoot;
        }

        // Parses `text` into `rom` starting at currentAddress. `end` is raised to the end of the
        // highest byte written. `source` names the file being parsed (null for the editor), for
        // error messages. Returns null on success, otherwise the error.
        private static string ParseInto(string text, byte[] rom, ref int currentAddress, ref int end,
                                         int depth, string source)
        {
            string at = source == null ? "" : $" in {source}";
            int tokenIndex = 0;
            int i = 0;
            int len = text.Length;

            while (i < len)
            {
                if (text[i] == '/' && i + 1 < len && text[i + 1] == '/')
                {
                    while (i < len && text[i] != '\n')
                        i++;
                    continue;
                }

                if (text[i] == '#')
                {
                    while (i < len && text[i] != '\n')
                        i++;
                    continue;
                }

                if (text[i] == '@')
                {
                    i++;
                    if (i + 4 <= len && string.Compare(text, i, "file", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                        && (i + 4 == len || char.IsWhiteSpace(text[i + 4]) || text[i + 4] == '"'))
                    {
                        i += 4;
                        while (i < len && (text[i] == ' ' || text[i] == '\t'))
                            i++;
                        string path;
                        if (i < len && text[i] == '"')
                        {
                            int close = text.IndexOf('"', i + 1);
                            if (close < 0)
                                return $"Missing closing '\"' after @file at token #{tokenIndex}{at}";
                            path = text.Substring(i + 1, close - i - 1);
                            i = close + 1;
                        }
                        else
                        {
                            int start = i;
                            while (i < len && !char.IsWhiteSpace(text[i]))
                                i++;
                            path = text.Substring(start, i - start);
                        }
                        if (path.Length == 0)
                            return $"Expected a file name after @file at token #{tokenIndex}{at}";
                        // LoadFile's errors already name the file they come from.
                        string error = LoadFile(path, rom, ref currentAddress, ref end, depth + 1);
                        if (error != null)
                            return error;
                        tokenIndex++;
                        continue;
                    }

                    if (i + 2 > len || text[i] != '0' || (text[i + 1] != 'x' && text[i + 1] != 'X'))
                    {
                        return $"Expected '0x' or 'file' after '@' at token #{tokenIndex}{at}";
                    }
                    i += 2;
                    int addr = 0;
                    int hexDigits = 0;
                    while (i < len && IsHexDigit(text[i]))
                    {
                        addr = (addr << 4) | HexValue(text[i]);
                        hexDigits++;
                        i++;
                        if (hexDigits > 4)
                        {
                            return $"Address too large at token #{tokenIndex}{at}";
                        }
                    }
                    currentAddress = addr;
                    tokenIndex++;
                    continue;
                }

                if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                {
                    i += 2;
                    int digitStart = i;
                    while (i < len && IsHexDigit(text[i]))
                        i++;
                    int digits = i - digitStart;
                    if (digits == 0)
                    {
                        return $"Expected hex digits after '0x' at token #{tokenIndex}{at}";
                    }
                    if (digits > 8)
                    {
                        return $"Hex value too large (max 8 digits) at token #{tokenIndex}{at}";
                    }
                    int byteCount = (digits + 1) / 2;
                    for (int b = 0; b < byteCount; b++)
                    {
                        int loPos = digits - 1 - 2 * b;
                        int hiPos = loPos - 1;
                        int lo = (loPos >= 0) ? HexValue(text[digitStart + loPos]) : 0;
                        int hi = (hiPos >= 0) ? HexValue(text[digitStart + hiPos]) : 0;
                        if (!Put(rom, ref currentAddress, ref end, (byte)((hi << 4) | lo)))
                        {
                            return $"Address {currentAddress} exceeds 65535 at token #{tokenIndex}{at}";
                        }
                    }
                    tokenIndex++;
                    continue;
                }

                if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'b' || text[i + 1] == 'B'))
                {
                    i += 2;
                    int val = 0;
                    int digits = 0;
                    while (i < len && (text[i] == '0' || text[i] == '1'))
                    {
                        val = (val << 1) | (text[i] - '0');
                        digits++;
                        i++;
                        if (digits > 8)
                        {
                            return $"Binary value too large at token #{tokenIndex}{at}";
                        }
                    }
                    if (digits == 0)
                    {
                        return $"Expected binary digits after '0b' at token #{tokenIndex}{at}";
                    }
                    if (!Put(rom, ref currentAddress, ref end, (byte)val))
                    {
                        return $"Address {currentAddress} exceeds 65535 at token #{tokenIndex}{at}";
                    }
                    tokenIndex++;
                    continue;
                }

                i++;
            }

            return null;
        }

        private static string LoadFile(string path, byte[] rom, ref int currentAddress, ref int end, int depth)
        {
            if (depth > MaxIncludeDepth)
                return $"@file {path}: nested more than {MaxIncludeDepth} levels deep (does a file include itself?)";

            string full = ResolvePath(path);
            byte[] bytes;
            try
            {
                if (!File.Exists(full))
                    return $"@file {path}: not found (looked for {full})";
                bytes = File.ReadAllBytes(full);
            }
            catch (Exception e)
            {
                return $"@file {path}: {e.Message}";
            }

            if (full.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                if (currentAddress + bytes.Length > rom.Length)
                    return $"@file {path}: {bytes.Length} bytes at address {currentAddress} go past 65535";
                foreach (byte b in bytes)
                    Put(rom, ref currentAddress, ref end, b);
                return null;
            }

            return ParseInto(Encoding.UTF8.GetString(bytes), rom, ref currentAddress, ref end, depth, path);
        }

        private static bool Put(byte[] rom, ref int currentAddress, ref int end, byte value)
        {
            if (currentAddress >= rom.Length) return false;
            rom[currentAddress++] = value;
            if (currentAddress > end) end = currentAddress;
            return true;
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return c - 'A' + 10;
        }
    }
}