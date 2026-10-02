"""Read-only PE/Go 1.18+ function map; never executes the input binary.

Format reference: https://go.dev/src/debug/gosym/pclntab.go
Outputs only selected function names/addresses, or a bounded explicitly requested
static byte range. No access to user profiles, process memory or credentials.
"""
import argparse
import bisect
import hashlib
import json
import re
import struct
from pathlib import Path


def functions(data):
    failures = []
    for match in re.finditer(b"[\xf0\xf1]\xff\xff\xff\x00\x00\x01\x08", data):
        base = match.start()
        i = -1
        try:
            count, _, text, names, _, _, _, table = struct.unpack_from("<8Q", data, base + 8)
            if not 1 <= count <= 1000000 or base + table + count * 8 + 4 > len(data):
                continue
            rows = []
            for i in range(count):
                entry, offset, end = struct.unpack_from("<III", data, base + table + i * 8)
                name_offset = struct.unpack_from("<i", data, base + table + offset + 4)[0]
                start = base + names + name_offset
                stop = data.find(b"\x00", start, start + 16384)
                if name_offset < 0 or start < base or stop <= start or end < entry:
                    raise ValueError(f"invalid function entry: entry={entry:x}, end={end:x}, nameOffset={name_offset}, terminator={stop}")
                name = data[start:stop].decode("utf-8")
                rows.append({"name": name, "start": hex(text + entry), "end": hex(text + end)})
            if rows:
                # A modern pcHeader may leave textStart zero. Resolve moduledata via
                # its first two pointers, then validate the code range against PE.
                if text == 0:
                    pe = struct.unpack_from("<I", data, 0x3c)[0]
                    image = struct.unpack_from("<Q", data, pe + 48)[0]
                    nsec = struct.unpack_from("<H", data, pe + 6)[0]
                    opt = struct.unpack_from("<H", data, pe + 20)[0]
                    sections = [struct.unpack_from("<4I", data, pe + 24 + opt + j * 40 + 8) for j in range(nsec)]
                    header_va = next(image + rva + base - raw for _, rva, size, raw in sections if raw <= base < raw + size)
                    pattern = struct.pack("<QQ", header_va, header_va + names)
                    candidates = []
                    examined = []
                    for module in re.finditer(re.escape(pattern), data):
                        minpc, maxpc, text_start, text_end = struct.unpack_from("<4Q", data, module.start() + 160)
                        examined.append([hex(module.start()), *map(hex, (minpc, maxpc, text_start, text_end)), rows[-1]["end"]])
                        if text_start == minpc and maxpc == text_start + int(rows[-1]["end"], 16) and 0 <= text_end - maxpc < 32:
                            candidates.append(text_start)
                    if len(candidates) != 1:
                        raise ValueError("Could not uniquely validate moduledata text address: " + repr(examined))
                    for row in rows:
                        row["start"] = hex(candidates[0] + int(row["start"], 16))
                        row["end"] = hex(candidates[0] + int(row["end"], 16))
                return base, rows, failures
        except (struct.error, UnicodeError, ValueError, StopIteration) as error:
            failures.append({"table": hex(base), "index": i, "reason": str(error)})
            continue
    raise ValueError("No validated Go function table found: " + json.dumps(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--match", default="NewTokenStorage|shouldBypassKeyring|KeyringTokenStorage|keyring\\.(Get|Set)")
    parser.add_argument("--static-va", type=lambda s: int(s, 0))
    parser.add_argument("--length", type=int, default=32)
    parser.add_argument("--headers", action="store_true")
    parser.add_argument("--callers", action="store_true", help="Candidate x86 E8 references, verify instruction boundaries with disassembly")
    parser.add_argument("--field-offset", type=lambda s: int(s, 0), help="Candidate Go reflection field metadata; not a type attribution")
    args = parser.parse_args()
    data = args.binary.read_bytes()
    if args.field_offset is not None:
        pe = struct.unpack_from("<I", data, 0x3c)[0]
        image = struct.unpack_from("<Q", data, pe + 48)[0]
        nsec = struct.unpack_from("<H", data, pe + 6)[0]
        opt = struct.unpack_from("<H", data, pe + 20)[0]
        sections = [struct.unpack_from("<4I", data, pe + 24 + opt + j * 40 + 8) for j in range(nsec)]
        fields = []
        for match in re.finditer(re.escape(struct.pack("<Q", args.field_offset)), data):
            at = match.start()
            if at < 16:
                continue
            ptr = struct.unpack_from("<Q", data, at - 16)[0]
            for _, rva, size, raw in sections:
                if image + rva <= ptr < image + rva + size - 130:
                    name_at = raw + ptr - image - rva
                    flags, length = data[name_at:name_at+2]
                    if flags <= 15 and 1 <= length <= 127:
                        name = data[name_at+2:name_at+2+length]
                        if re.fullmatch(b"[A-Za-z_][A-Za-z0-9_]*", name):
                            fields.append({"metadataOffset": hex(at-16), "name": name.decode(), "fieldOffset": hex(args.field_offset)})
        print(json.dumps(fields, indent=2))
        return
    if args.headers:
        print(json.dumps([{"offset": hex(m.start()), "header": data[m.start():m.start()+80].hex()}
                          for m in re.finditer(b"[\xf0\xf1\xfa\xfb]\xff\xff\xff\x00\x00[\x01\x02\x04][\x04\x08]", data)], indent=2))
        return
    if args.static_va is not None:
        if not 1 <= args.length <= 256:
            parser.error("length must be 1..256")
        pe = struct.unpack_from("<I", data, 0x3c)[0]
        if data[pe:pe + 4] != b"PE\x00\x00":
            raise ValueError("Not PE")
        sections = struct.unpack_from("<H", data, pe + 6)[0]
        optional_size = struct.unpack_from("<H", data, pe + 20)[0]
        image_base = struct.unpack_from("<Q", data, pe + 24 + 24)[0]
        rva = args.static_va - image_base
        for i in range(sections):
            at = pe + 24 + optional_size + i * 40
            _, address, size, offset = struct.unpack_from("<4I", data, at + 8)
            if address <= rva and rva + args.length <= address + size:
                value = data[offset + rva - address:offset + rva - address + args.length]
                print(json.dumps({"staticVA": hex(args.static_va), "bytes": value.hex(),
                                  "ascii": value.decode("ascii", errors="replace")}))
                return
        raise ValueError("Requested VA not backed by file")
    base, rows, failures = functions(data)
    selected = [row for row in rows if re.search(args.match, row["name"])]
    callers = []
    if args.callers:
        pe = struct.unpack_from("<I", data, 0x3c)[0]
        image = struct.unpack_from("<Q", data, pe + 48)[0]
        nsec = struct.unpack_from("<H", data, pe + 6)[0]
        opt = struct.unpack_from("<H", data, pe + 20)[0]
        targets = {int(row["start"], 16): row["name"] for row in selected}
        starts = [int(row["start"], 16) for row in rows]
        for j in range(nsec):
            at = pe + 24 + opt + j * 40
            _, rva, size, raw = struct.unpack_from("<4I", data, at + 8)
            flags = struct.unpack_from("<I", data, at + 36)[0]
            if not flags & 0x20000000:
                continue
            for match in re.finditer(b"\xe8", data[raw:raw + size - 4]):
                offset = match.start()
                site = image + rva + offset
                target = site + 5 + struct.unpack_from("<i", data, raw + offset + 1)[0]
                if target in targets:
                    idx = bisect.bisect_right(starts, site) - 1
                    caller = rows[idx]["name"] if idx >= 0 and site < int(rows[idx]["end"], 16) else "unknown"
                    callers.append({"site": hex(site), "target": targets[target], "caller": caller})
    print(json.dumps({"sha256": hashlib.sha256(data).hexdigest(), "pclntabOffset": hex(base),
                      "functionCount": len(rows), "rejectedTables": failures, "selected": selected, "candidateCalls": callers}, indent=2))


if __name__ == "__main__":
    main()
