#!/usr/bin/env python
r"""
Validates the QR encoder by decoding what it produces.

Run:  Harness\bin\Debug\Lumigram.Harness.exe qrdump dump.txt
      python Tools/verify-qr.py dump.txt

Decoding is the right test, not comparing against another encoder. Two encoders
can differ in padding after the terminator and both be perfectly valid - which is
exactly what happened here against segno, and chasing that difference wasted
time. What matters is whether a scanner reads back the original text.

Needs: pip install opencv-python-headless numpy
"""
import sys
import numpy as np
import cv2


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else 'qr-dump.txt'
    lines = open(path, encoding='ascii').read().split('\n')

    detector = cv2.QRCodeDetector()
    ok = failed = 0
    i = 0

    while i < len(lines):
        if not lines[i].startswith('### '):
            i += 1
            continue

        parts = lines[i][4:].split(' ', 2)
        text = parts[2] if len(parts) > 2 else ''
        size = int(lines[i + 1])
        rows = [lines[i + 2 + r] for r in range(size)]
        i += 2 + size

        quiet, scale = 4, 8
        side = (size + 2 * quiet) * scale
        img = np.ones((side, side), dtype=np.uint8) * 255
        for r in range(size):
            for c in range(size):
                if rows[r][c] == '1':
                    img[(r + quiet) * scale:(r + quiet + 1) * scale,
                        (c + quiet) * scale:(c + quiet + 1) * scale] = 0

        decoded, _, _ = detector.detectAndDecode(img)
        if decoded == text:
            print('  ok      %d chars, %dx%d' % (len(text), size, size))
            ok += 1
        else:
            print('  FAILED  %d chars, %dx%d -> %r' % (len(text), size, size, decoded[:40]))
            failed += 1

    print()
    print('%d decoded, %d failed' % (ok, failed))
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
