#!/usr/bin/env python3
"""Builds synthetic DVB muxes for testing without an aerial.

ffmpeg makes the audio/video, PAT, PMT and SDT; this script then swaps null
packets for a NIT (with UK-style logical channel numbers) and EIT (programme
guide) so the result looks like a real Freeview mux to the headend.

    tools/make_test_mux.py <output-dir>
"""
import datetime
import subprocess
import sys
from pathlib import Path

ONID = 0x233A
NETWORK_ID = 0x3005


def crc32_mpeg(data: bytes) -> int:
    crc = 0xFFFFFFFF
    for byte in data:
        crc ^= byte << 24
        for _ in range(8):
            crc = ((crc << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if crc & 0x80000000 else (crc << 1) & 0xFFFFFFFF
    return crc


def section(table_id: int, extension: int, body: bytes, version: int = 1, number: int = 0, last: int = 0) -> bytes:
    length = 5 + len(body) + 4
    head = bytes([table_id, 0xF0 | (length >> 8), length & 0xFF, extension >> 8, extension & 0xFF,
                  0xC0 | (version << 1) | 1, number, last])
    data = head + body
    return data + crc32_mpeg(data).to_bytes(4, "big")


def packetize(pid: int, data: bytes, cc: list) -> list:
    packets = []
    payload = b"\x00" + data
    first = True
    while payload:
        chunk, payload = payload[:184], payload[184:]
        header = bytes([0x47, (0x40 if first else 0) | (pid >> 8), pid & 0xFF, 0x10 | cc[0]])
        cc[0] = (cc[0] + 1) & 0x0F
        packets.append(header + chunk.ljust(184, b"\xFF"))
        first = False
    return packets


def descriptor(tag: int, payload: bytes) -> bytes:
    return bytes([tag, len(payload)]) + payload


def nit(transports: list) -> bytes:
    """transports: [(tsid, frequency_hz, [(service_id, lcn)])]"""
    network = descriptor(0x40, b"Test Network")
    loop = b""
    for tsid, frequency, services in transports:
        delivery = descriptor(0x5A, (frequency // 10).to_bytes(4, "big") + bytes([0x1F, 0x81, 0x00]) + b"\xFF" * 4)
        lcn = b"".join(sid.to_bytes(2, "big") + (0xFC00 | number).to_bytes(2, "big") for sid, number in services)
        descriptors = delivery + descriptor(0x83, lcn)
        loop += tsid.to_bytes(2, "big") + ONID.to_bytes(2, "big") + (0xF000 | len(descriptors)).to_bytes(2, "big") + descriptors
    body = (0xF000 | len(network)).to_bytes(2, "big") + network + (0xF000 | len(loop)).to_bytes(2, "big") + loop
    return section(0x40, NETWORK_ID, body)


def bcd(value: int) -> int:
    return ((value // 10) << 4) | (value % 10)


def eit(table_id: int, service_id: int, tsid: int, events: list) -> bytes:
    """events: [(event_id, start_datetime_utc, duration_seconds, title, text)]"""
    body = tsid.to_bytes(2, "big") + ONID.to_bytes(2, "big") + bytes([0, table_id])
    for event_id, start, duration, title, text in events:
        mjd = (start.date() - datetime.date(1858, 11, 17)).days
        short = descriptor(0x4D, b"eng" + bytes([len(title)]) + title.encode("latin-1") +
                           bytes([len(text)]) + text.encode("latin-1"))
        content = descriptor(0x54, bytes([0x20, 0x00]))
        descriptors = short + content
        body += event_id.to_bytes(2, "big") + mjd.to_bytes(2, "big")
        body += bytes([bcd(start.hour), bcd(start.minute), bcd(start.second)])
        body += bytes([bcd(duration // 3600), bcd(duration // 60 % 60), bcd(duration % 60)])
        body += (0x8000 | len(descriptors)).to_bytes(2, "big") + descriptors
    return section(table_id, service_id, body)


def inject(ts_path: Path, si_packets: list) -> None:
    """Replaces null packets with SI, repeating the SI set through the file."""
    data = bytearray(ts_path.read_bytes())
    index = 0
    replaced = 0
    for offset in range(0, len(data) - 187, 188):
        pid = ((data[offset + 1] & 0x1F) << 8) | data[offset + 2]
        if pid == 0x1FFF:
            data[offset:offset + 188] = si_packets[index % len(si_packets)]
            index += 1
            replaced += 1
    ts_path.write_bytes(data)
    print(f"{ts_path.name}: {len(data) // 188} packets, {replaced} null packets replaced "
          f"({replaced // len(si_packets)} full SI cycles)")


def ffmpeg(args: list) -> None:
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y"] + args, check=True)


def main() -> None:
    out = Path(sys.argv[1] if len(sys.argv) > 1 else "testdata")
    out.mkdir(parents=True, exist_ok=True)
    seconds = "20"

    # Mux A (DVB-T style): an SD MPEG-2 TV service plus a radio service.
    mux_a = out / "mux_a.ts"
    ffmpeg(["-f", "lavfi", "-i", f"testsrc2=size=720x576:rate=25:duration={seconds}",
            "-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}",
            "-f", "lavfi", "-i", f"sine=frequency=880:duration={seconds}",
            "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "mpeg2video", "-b:v", "2500k", "-g", "12", "-c:a", "mp2", "-b:a", "128k",
            "-metadata:s:a:0", "language=eng",
            "-program", "title=BBC ONE Test:program_num=4164:st=0:st=1",
            "-program", "title=BBC Radio 4 Test:program_num=6720:st=2",
            "-mpegts_transport_stream_id", str(0x1041), "-mpegts_original_network_id", str(ONID),
            "-metadata", "service_provider=Test", "-muxrate", "5000k", "-f", "mpegts", str(mux_a)])

    # Mux B (DVB-T2 style): an H.264 HD service.
    mux_b = out / "mux_b.ts"
    ffmpeg(["-f", "lavfi", "-i", f"testsrc2=size=1280x720:rate=25:duration={seconds}",
            "-f", "lavfi", "-i", f"sine=frequency=660:duration={seconds}",
            "-map", "0:v", "-map", "1:a",
            "-c:v", "libx264", "-preset", "veryfast", "-b:v", "3000k", "-g", "25", "-c:a", "aac", "-b:a", "128k",
            "-program", "title=BBC ONE HD Test:program_num=17540:st=0:st=1",
            "-mpegts_transport_stream_id", str(0x4084), "-mpegts_original_network_id", str(ONID),
            "-metadata", "service_provider=Test", "-muxrate", "5000k", "-f", "mpegts", str(mux_b)])

    transports = [
        (0x1041, 674_000_000, [(4164, 1), (6720, 704)]),
        (0x4084, 626_000_000, [(17540, 101)]),
    ]
    now = datetime.datetime.now(datetime.timezone.utc).replace(minute=0, second=0, microsecond=0, tzinfo=None)

    def schedule(name: str, first_id: int) -> list:
        return [(first_id + i, now + datetime.timedelta(hours=i), 3600, f"{name} show {i + 1}",
                 f"Episode {i + 1} of the {name} test schedule. Cafe £10.") for i in range(6)]

    for path, own_tsid in ((mux_a, 0x1041), (mux_b, 0x4084)):
        packets = []
        cc_nit, cc_eit = [0], [0]
        packets += packetize(0x10, nit(transports), cc_nit)
        for sid, tsid, name in ((4164, 0x1041, "BBC ONE"), (6720, 0x1041, "Radio 4"), (17540, 0x4084, "BBC ONE HD")):
            table_id = 0x50 if tsid == own_tsid else 0x60
            packets += packetize(0x12, eit(table_id, sid, tsid, schedule(name, sid % 1000 * 10)), cc_eit)
        inject(path, packets)


if __name__ == "__main__":
    main()
