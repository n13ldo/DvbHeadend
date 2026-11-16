#!/usr/bin/env python3
"""Converts the Freesat/Freeview HD Huffman tables into the binary resource
the headend embeds (src/DvbHeadend/Ts/freesat_huffman.bin).

The source is tvheadend's src/epggrab/support/freesat_huffman.c (GPL-3.0):

    tools/make_huffman_tables.py <path-to-freesat_huffman.c> <output.bin>

Layout, little-endian, for table 1 then table 2:
    uint32 entry_count
    entry_count x { uint32 code (left-aligned), uint8 bit_length, uint8 next_char }
    129 x uint32 index      (entries for previous char c are index[c] .. index[c+1]-1)
"""
import re
import struct
import sys


def main() -> None:
    source = open(sys.argv[1], encoding="latin-1").read()
    out = bytearray()
    for n in (1, 2):
        table = re.search(r"struct fsattab fsat_table_%d\[\] = \{(.*?)\n\};" % n, source, re.S).group(1)
        entries = re.findall(r"\{\s*0x([0-9a-fA-F]+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\}", table)
        if len(entries) != table.count("{"):
            raise SystemExit(f"table {n}: parsed {len(entries)} of {table.count('{')} entries")
        index_text = re.search(r"unsigned fsat_index_%d\[\] = \{(.*?)\n\};" % n, source, re.S).group(1)
        index = [int(v) for v in re.findall(r"^\s*(\d+)\s*,?\s*/\*", index_text, re.M)]
        if len(index) != 129 or index[-1] != len(entries):
            raise SystemExit(f"table {n}: bad index ({len(index)} entries, last {index[-1]}, table {len(entries)})")
        out += struct.pack("<I", len(entries))
        for code, bits, nxt in entries:
            out += struct.pack("<IBB", int(code, 16), int(bits), int(nxt))
        out += struct.pack("<129I", *index)
        print(f"table {n}: {len(entries)} entries")
    open(sys.argv[2], "wb").write(out)
    print(f"wrote {len(out)} bytes")


if __name__ == "__main__":
    main()
