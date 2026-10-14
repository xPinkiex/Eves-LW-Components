using System;

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
//   // comment      single-line comment (ignored until newline)
//   #  comment      single-line comment (ignored until newline)
//
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

        public static ParseResult Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ParseResult { Success = true, Data = new byte[0], ByteCount = 0 };
            }

            var rom = new byte[65536];
            int currentAddress = 0;
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
                    if (i + 2 > len || text[i] != '0' || (text[i + 1] != 'x' && text[i + 1] != 'X'))
                    {
                        return new ParseResult
                        {
                            Success = false,
                            Error = $"Expected '0x' after '@' at token #{tokenIndex}",
                        };
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
                            return new ParseResult
                            {
                                Success = false,
                                Error = $"Address too large at token #{tokenIndex}",
                            };
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
                        return new ParseResult
                        {
                            Success = false,
                            Error = $"Expected hex digits after '0x' at token #{tokenIndex}",
                        };
                    }
                    if (digits > 8)
                    {
                        return new ParseResult
                        {
                            Success = false,
                            Error = $"Hex value too large (max 8 digits) at token #{tokenIndex}",
                        };
                    }
                    int byteCount = (digits + 1) / 2;
                    for (int b = 0; b < byteCount; b++)
                    {
                        int loPos = digits - 1 - 2 * b;
                        int hiPos = loPos - 1;
                        int lo = (loPos >= 0) ? HexValue(text[digitStart + loPos]) : 0;
                        int hi = (hiPos >= 0) ? HexValue(text[digitStart + hiPos]) : 0;
                        if (currentAddress >= rom.Length)
                        {
                            return new ParseResult
                            {
                                Success = false,
                                Error = $"Address {currentAddress} exceeds 65535 at token #{tokenIndex}",
                            };
                        }
                        rom[currentAddress] = (byte)((hi << 4) | lo);
                        currentAddress++;
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
                            return new ParseResult
                            {
                                Success = false,
                                Error = $"Binary value too large at token #{tokenIndex}",
                            };
                        }
                    }
                    if (digits == 0)
                    {
                        return new ParseResult
                        {
                            Success = false,
                            Error = $"Expected binary digits after '0b' at token #{tokenIndex}",
                        };
                    }
                    if (currentAddress >= rom.Length)
                    {
                        return new ParseResult
                        {
                            Success = false,
                            Error = $"Address {currentAddress} exceeds 65535 at token #{tokenIndex}",
                        };
                    }
                    rom[currentAddress] = (byte)val;
                    currentAddress++;
                    tokenIndex++;
                    continue;
                }

                i++;
            }

            int actualLength = currentAddress;
            byte[] result = new byte[actualLength];
            Array.Copy(rom, result, actualLength);

            return new ParseResult
            {
                Success = true,
                Data = result,
                ByteCount = actualLength,
            };
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