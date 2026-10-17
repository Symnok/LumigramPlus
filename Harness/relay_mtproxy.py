# -*- coding: utf-8 -*-
# An MTProto proxy that really relays to Telegram, written from the transport documentation and
# TDLib (TlsInit.cpp, TcpTransport.cpp) - independently of the C# client it tests.
#
#   python relay_mtproxy.py <secret-hex> <port> [connections]
#
# Secret 16 bytes: plain obfuscated2. dd+16: padded. ee+16+domain: FakeTLS.
# For FakeTLS it behaves like a real proxy: checks the ClientHello HMAC and timestamp, answers
# with ServerHello + ChangeCipherSpec + an application-data record of RANDOM FILLER (which the
# client must discard), accepts the client's ChangeCipherSpec, then carries the stream in records.
# After the 64-byte init it connects to the datacenter named in it and relays both ways.
import hashlib
import hmac
import os
import socket
import struct
import sys
import threading
import time

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

SECRET = bytes.fromhex(sys.argv[1])
PORT = int(sys.argv[2])
CONNECTIONS = int(sys.argv[3]) if len(sys.argv) > 3 else 1

KEY = SECRET if len(SECRET) == 16 else SECRET[1:17]
FAKETLS = len(SECRET) >= 18 and SECRET[0] == 0xee
DOMAIN = SECRET[17:].decode() if FAKETLS else None

DCS = {1: '149.154.175.50', 2: '149.154.167.51', 3: '149.154.175.100', 4: '149.154.167.91',
       5: '149.154.171.5', 10002: '149.154.167.40'}

lock = threading.Lock()


def log(cid, *parts):
    with lock:
        print('[%d] ' % cid + ' '.join(str(p) for p in parts))
        sys.stdout.flush()


def ctr(key, iv):
    return Cipher(algorithms.AES(key), modes.CTR(iv)).encryptor()


def read_exactly(conn, n):
    buf = b''
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            raise EOFError('closed after %d of %d bytes' % (len(buf), n))
        buf += chunk
    return buf


class ClientStream:
    """Bytes from the client, with TLS records stripped when FakeTLS."""

    def __init__(self, conn, cid):
        self.conn, self.cid, self.buf, self.ccs_seen = conn, cid, b'', 0

    def read(self, n):
        if not FAKETLS:
            return read_exactly(self.conn, n)
        while len(self.buf) < n:
            h = read_exactly(self.conn, 5)
            body = read_exactly(self.conn, struct.unpack('>H', h[3:5])[0])
            if h[0] == 0x14:
                self.ccs_seen += 1
                continue
            if h[0] != 0x17 or h[1:3] != b'\x03\x03':
                raise ValueError('client record type 0x%02x %s' % (h[0], h[1:3].hex()))
            self.buf += body
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def read_some(self):
        if not FAKETLS:
            data = self.conn.recv(65536)
            if not data:
                raise EOFError('client closed')
            return data
        if self.buf:
            out, self.buf = self.buf, b''
            return out
        return self.read(1) + self.read_buffered()

    def read_buffered(self):
        out, self.buf = self.buf, b''
        return out


def tls_handshake(conn, cid):
    head = read_exactly(conn, 5)
    if head[0] != 0x16:
        raise ValueError('first record 0x%02x is not a handshake' % head[0])
    hello = head + read_exactly(conn, struct.unpack('>H', head[3:5])[0])
    claimed = hello[11:43]
    expected = bytearray(hmac.new(KEY, hello[:11] + b'\x00' * 32 + hello[43:], hashlib.sha256).digest())
    stamp = struct.unpack('<i', bytes(a ^ b for a, b in zip(claimed[28:32], expected[28:32])))[0]
    expected[28:32] = claimed[28:32]
    ok = hmac.compare_digest(bytes(expected), claimed)
    sni_ok = DOMAIN.encode() in hello
    log(cid, 'hello', len(hello), 'bytes  digest_ok', ok, ' skew', stamp - int(time.time()), 's  sni_present', sni_ok)
    if not ok or abs(stamp - int(time.time())) > 120:
        raise ValueError('ClientHello rejected')

    body = (b'\x03\x03' + b'\x00' * 32 + b'\x20' + hello[44:76] + b'\x13\x01\x00'
            + b'\x00\x2e' + b'\x00\x2b\x00\x02\x03\x04'
            + b'\x00\x33\x00\x24\x00\x1d\x00\x20' + os.urandom(32))
    hs = b'\x02' + struct.pack('>I', len(body))[1:] + body
    filler = os.urandom(1024 + os.urandom(1)[0] * 4)        # what real proxies send: noise
    reply = (b'\x16\x03\x03' + struct.pack('>H', len(hs)) + hs
             + b'\x14\x03\x03\x00\x01\x01'
             + b'\x17\x03\x03' + struct.pack('>H', len(filler)) + filler)
    digest = hmac.new(KEY, claimed + reply, hashlib.sha256).digest()  # digest field is zero here
    conn.sendall(reply[:11] + digest + reply[43:])
    log(cid, 'server hello sent with', len(filler), 'bytes of filler')


def serve(conn, cid):
    if FAKETLS:
        tls_handshake(conn, cid)
    stream = ClientStream(conn, cid)

    init = stream.read(64)
    dec = ctr(hashlib.sha256(init[8:40] + KEY).digest(), init[40:56])
    plain = dec.update(init)
    tag = plain[56:60]
    dc = struct.unpack('<h', plain[60:62])[0]
    rev = init[::-1]
    enc = ctr(hashlib.sha256(rev[8:40] + KEY).digest(), rev[40:56])
    log(cid, 'init: tag', tag.hex(), ' dc', dc)
    if tag not in (b'\xee' * 4, b'\xdd' * 4):
        raise ValueError('bad tag - keys disagree')
    if len(SECRET) >= 17 and tag != b'\xdd' * 4:
        raise ValueError('a dd/ee secret must use padded intermediate')

    dc_sock = socket.create_connection((DCS[dc], 443), timeout=30)
    dc_sock.sendall(tag)
    log(cid, 'relaying to DC', dc, DCS[dc])

    totals = {'up': 0, 'down': 0}

    def down():
        try:
            while True:
                data = dc_sock.recv(65536)
                if not data:
                    break
                totals['down'] += len(data)
                data = enc.update(data)
                if FAKETLS:
                    out = b''
                    for i in range(0, len(data), 16384):
                        part = data[i:i + 16384]
                        out += b'\x17\x03\x03' + struct.pack('>H', len(part)) + part
                    data = out
                conn.sendall(data)
        except Exception:
            pass
        finally:
            try:
                conn.shutdown(socket.SHUT_RDWR)
            except Exception:
                pass

    t = threading.Thread(target=down, daemon=True)
    t.start()
    try:
        first = stream.read_buffered()
        if first:
            totals['up'] += len(first)
            dc_sock.sendall(dec.update(first))
        while True:
            data = stream.read_some()
            totals['up'] += len(data)
            dc_sock.sendall(dec.update(data))
    except EOFError:
        pass
    finally:
        dc_sock.close()
        t.join(5)
        log(cid, 'closed: up', totals['up'], 'down', totals['down'], ' client CCS records', stream.ccs_seen)


srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(('127.0.0.1', PORT))
srv.listen(4)
print('relay MTProxy on 127.0.0.1:%d  mode=%s%s' % (
    PORT, 'faketls' if FAKETLS else ('padded' if len(SECRET) == 17 else 'plain'),
    ('  domain=' + DOMAIN) if FAKETLS else ''))
sys.stdout.flush()

srv.settimeout(120)
threads = []
for cid in range(1, CONNECTIONS + 1):
    try:
        conn, _ = srv.accept()
    except socket.timeout:
        print('no more connections')
        break

    def run(c=conn, i=cid):
        try:
            serve(c, i)
        except Exception as exc:                            # noqa: BLE001
            log(i, 'ERROR %s: %s' % (type(exc).__name__, exc))
        finally:
            c.close()

    th = threading.Thread(target=run, daemon=True)
    th.start()
    threads.append(th)

for th in threads:
    th.join(90)
srv.close()
print('done')
