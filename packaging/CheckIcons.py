"""Verify the first Windows shell icon group in built PE files, using only Python's standard library."""
import argparse
import hashlib
import json
import struct
from pathlib import Path


def icon_frames(data):
    reserved, kind, count = struct.unpack_from("<HHH", data)
    if reserved != 0 or kind != 1 or count == 0:
        raise ValueError("Invalid source ICO")
    frames = []
    for i in range(count):
        size, offset = struct.unpack_from("<II", data, 6 + 16 * i + 8)
        frames.append(data[offset:offset + size])
    return frames


def shell_icon_frames(data):
    pe = struct.unpack_from("<I", data, 60)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Expected a PE executable")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    resource_rva = struct.unpack_from("<I", data, optional + (112 if magic == 0x20b else 96) + 16)[0]
    sections = [struct.unpack_from("<8sIIII", data, optional + optional_size + 40 * i) for i in range(count)]

    def offset(rva):
        for _, virtual_size, address, size, start in sections:
            if address <= rva < address + max(size, virtual_size):
                return start + rva - address
        raise ValueError("Unmapped resource RVA")

    base = offset(resource_rva)

    def entries(directory):
        named, ids = struct.unpack_from("<HH", data, base + directory + 12)
        return [struct.unpack_from("<II", data, base + directory + 16 + i * 8) for i in range(named + ids)]

    resources = {}

    def walk(directory, path):
        for name, value in entries(directory):
            if value & 0x80000000:
                walk(value & 0x7fffffff, path + (name,))
            else:
                rva, size = struct.unpack_from("<II", data, base + value)
                resources[path + (name,)] = data[offset(rva):offset(rva) + size]

    walk(0, ())
    group_key = min(key for key in resources if key[0] == 14)
    group = resources[group_key]
    frame_count = struct.unpack_from("<H", group, 4)[0]
    frames = []
    for i in range(frame_count):
        icon_id = struct.unpack_from("<H", group, 6 + i * 14 + 12)[0]
        frames.append(resources[(3, icon_id, group_key[2])])
    return frames


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--icon", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("executables", nargs="+", type=Path)
    args = parser.parse_args()
    expected = sorted(hashlib.sha256(frame).hexdigest() for frame in icon_frames(args.icon.read_bytes()))
    results = []
    for executable in args.executables:
        actual = sorted(hashlib.sha256(frame).hexdigest() for frame in shell_icon_frames(executable.read_bytes()))
        if actual != expected:
            raise ValueError("Shell icon differs from sheep.ico: " + executable.name)
        results.append({"File": executable.name, "Frames": len(actual), "SheepIconMatches": True})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print("PASS sheep shell icons: " + ", ".join(item["File"] for item in results))


if __name__ == "__main__":
    main()
