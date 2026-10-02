"""Synthetic tests only; no installed binary, filesystem writes or credentials."""
import importlib.util
import struct
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("function_map", Path(__file__).with_name("Inspect-GoFunctionMap.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def fixture(name=b"fixture.One", text=0x140001000):
    data = bytearray(0x6000)
    base, names, table = 0x400, 0x48, 0x4800
    data[base:base+8] = b"\xf1\xff\xff\xff\0\0\1\10"
    struct.pack_into("<8Q", data, base+8, 2, 1, text, names, 0, 0, 0, table)
    name_bytes = name + b"\0fixture.Two\0"
    data[base+names:base+names+len(name_bytes)] = name_bytes
    struct.pack_into("<5I", data, base+table, 0, 32, 16, 48, 32)
    struct.pack_into("<Ii", data, base+table+32, 0, 0)
    struct.pack_into("<Ii", data, base+table+48, 16, len(name)+1)
    # Minimal PE section mapping and moduledata for an unrelocated pcHeader.
    struct.pack_into("<I", data, 0x3c, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", data, 0x86, 2)
    struct.pack_into("<H", data, 0x94, 0x70)
    struct.pack_into("<Q", data, 0xb0, 0x140000000)
    struct.pack_into("<4I", data, 0x110, 0x200, 0x1000, 0x200, 0x200)
    struct.pack_into("<4I", data, 0x138, 0x5c00, 0x2000, 0x5c00, 0x400)
    struct.pack_into("<QQ", data, 0x5400, 0x140002000, 0x140002048)
    struct.pack_into("<4Q", data, 0x5400+160, 0x140001000, 0x140001020, 0x140001000, 0x140001020)
    return data


class FunctionMapTests(unittest.TestCase):
    def test_explicit_text(self):
        base, rows, failures = module.functions(fixture())
        self.assertEqual(base, 0x400)
        self.assertEqual(rows[0], {"name": "fixture.One", "start": "0x140001000", "end": "0x140001010"})
        self.assertEqual(len(rows), 2)
        self.assertEqual(failures, [])

    def test_moduledata_resolution(self):
        self.assertEqual(module.functions(fixture(text=0))[1][0]["start"], "0x140001000")

    def test_long_generic_name(self):
        self.assertEqual(len(module.functions(fixture(b"x"*2048))[1][0]["name"]), 2048)

    def test_truncated_header(self):
        with self.assertRaises(ValueError):
            module.functions(b"\xf1\xff\xff\xff\0\0\1\10")

    def test_bad_name_offset(self):
        data = fixture()
        struct.pack_into("<i", data, 0x400+0x4800+36, -1)
        with self.assertRaises(ValueError):
            module.functions(data)

    def test_descending_entries(self):
        data = fixture()
        struct.pack_into("<I", data, 0x400+0x4800, 17)
        with self.assertRaises(ValueError):
            module.functions(data)

    def test_ambiguous_moduledata(self):
        data = fixture(text=0)
        data[0x5600:0x56c0] = data[0x5400:0x54c0]
        with self.assertRaises(ValueError):
            module.functions(data)


if __name__ == "__main__":
    unittest.main()
