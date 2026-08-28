#!/usr/bin/env python
"""
Checks an OGG Opus file the way a real decoder would.

    python Tools/verify-ogg.py <file.opus> [more files...]

Written for the same reason as verify-qr.py, and after the same mistake. Our own
reader does not verify page checksums, so running our output back through it
proves only that we agree with ourselves - which is exactly how a QR encoder once
passed every test and would not scan.

So this is deliberately a second implementation, from the specification, in a
different language: page framing, the segment table, packet reassembly, and above
all the checksum, which is the field most likely to be quietly wrong. OGG uses
polynomial 0x04c11db7 applied MSB-first with no reflection and no final
inversion - not zlib's CRC-32, which reflects both ends and inverts them.

Point it at a file from a known-good encoder first. A checker that cannot
validate the reference is not evidence about anything else.
"""

import sys
import struct


def crc(data):
    """OGG's CRC-32: MSB-first, init 0, no reflection, no final xor."""
    reg = 0
    for byte in data:
        reg ^= byte << 24
        reg &= 0xFFFFFFFF
        for _ in range(8):
            if reg & 0x80000000:
                reg = ((reg << 1) ^ 0x04C11DB7) & 0xFFFFFFFF
            else:
                reg = (reg << 1) & 0xFFFFFFFF
    return reg


def pages(data):
    """Yields (header_type, granule, serial, sequence, crc_ok, segments, body)."""
    at = 0
    while at + 27 <= len(data):
        if data[at:at + 4] != b'OggS':
            raise ValueError('no OggS capture pattern at byte %d' % at)

        version = data[at + 4]
        if version != 0:
            raise ValueError('unknown page version %d at byte %d' % (version, at))

        header_type = data[at + 5]
        granule = struct.unpack_from('<q', data, at + 6)[0]
        serial = struct.unpack_from('<I', data, at + 14)[0]
        sequence = struct.unpack_from('<I', data, at + 18)[0]
        stored = struct.unpack_from('<I', data, at + 22)[0]
        count = data[at + 26]

        table_at = at + 27
        body_at = table_at + count
        if body_at > len(data):
            raise ValueError('segment table runs past end of file')

        segments = list(data[table_at:body_at])
        body_length = sum(segments)
        end = body_at + body_length
        if end > len(data):
            raise ValueError('page body runs past end of file')

        page = bytearray(data[at:end])
        page[22:26] = b'\x00\x00\x00\x00'
        crc_ok = crc(bytes(page)) == stored

        yield header_type, granule, serial, sequence, crc_ok, segments, data[body_at:end]
        at = end


def check(path):
    with open(path, 'rb') as handle:
        data = handle.read()

    print('%s  %d bytes' % (path, len(data)))

    packets = []
    current = bytearray()
    bad_crc = 0
    page_count = 0
    last_granule = 0
    saw_bos = False
    saw_eos = False
    expected_sequence = 0
    problems = []

    for header_type, granule, serial, sequence, crc_ok, segments, body in pages(data):
        page_count += 1
        if not crc_ok:
            bad_crc += 1

        if header_type & 0x02:
            saw_bos = True
            if page_count != 1:
                problems.append('beginning-of-stream flag on page %d' % page_count)
        if header_type & 0x04:
            saw_eos = True

        if sequence != expected_sequence:
            problems.append('page sequence jumped: expected %d, got %d'
                            % (expected_sequence, sequence))
        expected_sequence = sequence + 1

        if not (header_type & 0x01):
            if current:
                problems.append('page %d starts a packet while one is unfinished'
                                % page_count)
            current = bytearray()

        at = 0
        for length in segments:
            current += body[at:at + length]
            at += length
            if length != 255:
                packets.append(bytes(current))
                current = bytearray()

        if granule != -1:
            last_granule = granule

    if current:
        problems.append('file ends mid-packet')
    if not saw_bos:
        problems.append('no beginning-of-stream page')
    if not saw_eos:
        problems.append('no end-of-stream page')
    if bad_crc:
        problems.append('%d page(s) with a bad checksum' % bad_crc)

    if len(packets) < 2:
        problems.append('fewer than the two mandatory header packets')
    else:
        if not packets[0].startswith(b'OpusHead'):
            problems.append('first packet is not OpusHead')
        else:
            channels = packets[0][9]
            pre_skip = struct.unpack_from('<H', packets[0], 10)[0]
            rate = struct.unpack_from('<I', packets[0], 12)[0]
            print('  OpusHead : %d channel(s), pre-skip %d, input rate %d'
                  % (channels, pre_skip, rate))
        if not packets[1].startswith(b'OpusTags'):
            problems.append('second packet is not OpusTags')

    audio = len(packets) - 2
    seconds = last_granule / 48000.0
    print('  pages    : %d' % page_count)
    print('  packets  : %d audio' % audio)
    print('  granule  : %d  (%.2f s at 48 kHz)' % (last_granule, seconds))
    print('  checksums: %s' % ('all valid' if not bad_crc else '%d BAD' % bad_crc))

    for problem in problems:
        print('  PROBLEM  : %s' % problem)

    print('  %s' % ('PASS' if not problems else 'FAILED'))
    print('')
    return not problems


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    ok = True
    for path in sys.argv[1:]:
        try:
            ok = check(path) and ok
        except Exception as error:
            print('  FAILED: %s\n' % error)
            ok = False

    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
