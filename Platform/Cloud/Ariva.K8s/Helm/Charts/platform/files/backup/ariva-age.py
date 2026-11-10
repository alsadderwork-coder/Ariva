#!/usr/bin/env python3
# Ariva scheduled backup (ARV-182): encrypts standard input to standard output in the age v1 format
# (https://age-encryption.org/v1, the C2SP age specification) for one X25519 recipient, the backup custodian's public
# recipient (age1...). The chart's CronJob runs it in a pod of the TimescaleDB image, which ships Python 3 and pyca/cryptography
# but not the age binary; the file it writes is decrypted with age itself (age --decrypt --identity <identity>), by
# scripts/ops/ariva-ops.mjs restore. Only public material is ever here: no identity, no signing key.
#
#   python3 ariva-age.py --recipient age1...   < plaintext > ciphertext
#   python3 ariva-age.py --recipient age1... --digest FILE   < plaintext > ciphertext
#                       and then writes "<SHA-256> <bytes>" of exactly the bytes it wrote to standard output into FILE, a new
#                       file (refused if it exists) in a private folder of the caller's: the hash of the stream, which the
#                       runner holds the published file to, so a file put in its place on the claim is caught (ARV-182 review)
#   python3 ariva-age.py --recipient age1... --check   checks the recipient (its Bech32 checksum and key) and writes nothing
#
# What it writes, per the specification:
# - a 16-byte file key from the operating system's random source (os.urandom), one per file;
# - one X25519 stanza: a new ephemeral key pair per file, the shared secret with the recipient (refused when it is all zero),
#   the wrap key HKDF-SHA-256(shared, salt = ephemeral share || recipient, info "age-encryption.org/v1/X25519") and the file
#   key sealed with ChaCha20-Poly1305 under it (nonce zero, the key used once);
# - the header MAC, HMAC-SHA-256 under HKDF-SHA-256(file key, no salt, info "header") over the header up to and with "---";
# - a 16-byte payload nonce, the payload key HKDF-SHA-256(file key, salt = nonce, info "payload"), and the payload as
#   ChaCha20-Poly1305 chunks of 64 KiB (STREAM): an 11-byte big-endian counter and a last-chunk flag as each nonce, the last
#   chunk flagged 1 and never empty unless the whole payload is.
# The primitives are pyca/cryptography's (OpenSSL); this file only lays them out as the format says. Its output is proven
# against age itself and against an independent reading of the format (Platform/Cloud/Ariva.K8s/tests/backup-age.mjs,
# Ariva.IntegrationTests ScheduledBackupTests).
#
# Errors name the problem and never repeat a value given (an identity pasted as the recipient is refused without echo).
# Exit codes: 0 done, 1 failed while writing, 2 refused input.
import base64
import hashlib
import hmac
import os
import re
import sys

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

CHUNK = 64 * 1024
MAX_CHUNKS = 1 << 88
CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l"
# Matched whole (fullmatch): "$" would also take a trailing line break.
RECIPIENT = re.compile(r"age1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}")
DIGEST_PATH = re.compile(r"/[A-Za-z0-9._/-]{1,200}")


def refuse(message):
    sys.stderr.write("ariva-age: refused: " + message + "\n")
    sys.exit(2)


def polymod(values):
    generator = [0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3]
    check = 1
    for value in values:
        top = check >> 25
        check = ((check & 0x1FFFFFF) << 5) ^ value
        for i in range(5):
            if (top >> i) & 1:
                check ^= generator[i]
    return check


def recipient_key(text):
    """The 32-byte X25519 public key of an age recipient: Bech32 (not Bech32m), human-readable part "age"."""
    if "AGE-SECRET-KEY-" in text.upper():
        refuse("--recipient holds an age identity (a private key); give the public recipient (age1...) only")
    if not RECIPIENT.fullmatch(text):
        refuse("--recipient is not an age X25519 recipient (age1 and 58 lower-case Bech32 characters)")
    data = [CHARSET.index(c) for c in text[4:]]
    hrp = [ord(c) >> 5 for c in "age"] + [0] + [ord(c) & 31 for c in "age"]
    if polymod(hrp + data) != 1:
        refuse("--recipient has a wrong checksum (a character was changed or lost)")
    bits = 0
    accumulator = 0
    key = bytearray()
    for value in data[:-6]:
        accumulator = (accumulator << 5) | value
        bits += 5
        if bits >= 8:
            bits -= 8
            key.append((accumulator >> bits) & 0xFF)
    if len(key) != 32 or bits >= 5 or (accumulator & ((1 << bits) - 1)) != 0:
        refuse("--recipient does not hold a 32-byte key")
    return bytes(key)


def hkdf(secret, salt, info):
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=salt, info=info).derive(secret)


def b64(data):
    return base64.b64encode(data).rstrip(b"=")


def header(recipient, file_key):
    ephemeral = X25519PrivateKey.generate()
    share = ephemeral.public_key().public_bytes(encoding=serialization.Encoding.Raw, format=serialization.PublicFormat.Raw)
    try:
        shared = ephemeral.exchange(X25519PublicKey.from_public_bytes(recipient))
    except ValueError:
        refuse("--recipient is not a usable X25519 key")
    if shared == bytes(32):
        refuse("--recipient is not a usable X25519 key")
    wrap_key = hkdf(shared, share + recipient, b"age-encryption.org/v1/X25519")
    body = ChaCha20Poly1305(wrap_key).encrypt(bytes(12), file_key, None)
    # Each stanza body line holds at most 64 columns and the last one fewer: the 32-byte body is one line of 43.
    text = b"age-encryption.org/v1\n-> X25519 " + b64(share) + b"\n" + b64(body) + b"\n---"
    mac = hmac.new(hkdf(file_key, b"", b"header"), text, hashlib.sha256).digest()
    return text + b" " + b64(mac) + b"\n"


def read_full(source, size):
    parts = []
    left = size
    while left > 0:
        part = source.read(left)
        if not part:
            break
        parts.append(part)
        left -= len(part)
    return b"".join(parts)


class Counted:
    """The sink, with the SHA-256 and the length of every byte written to it."""

    def __init__(self, sink):
        self.sink = sink
        self.hash = hashlib.sha256()
        self.length = 0

    def write(self, data):
        self.sink.write(data)
        self.hash.update(data)
        self.length += len(data)


def encrypt(source, sink, recipient):
    file_key = os.urandom(16)
    sink.write(header(recipient, file_key))
    nonce = os.urandom(16)
    sink.write(nonce)
    stream = ChaCha20Poly1305(hkdf(file_key, nonce, b"payload"))
    counter = 0
    chunk = read_full(source, CHUNK)
    while True:
        following = read_full(source, CHUNK) if len(chunk) == CHUNK else b""
        last = len(following) == 0
        if counter >= MAX_CHUNKS:
            raise OverflowError("the payload is longer than the format allows")
        sink.write(stream.encrypt(counter.to_bytes(11, "big") + (b"\x01" if last else b"\x00"), chunk, None))
        if last:
            return
        counter += 1
        chunk = following


def main(argv):
    check = argv[2:] == ["--check"]
    digest = argv[3] if len(argv) == 4 and argv[2] == "--digest" else None
    if len(argv) < 2 or argv[0] != "--recipient" or not (len(argv) == 2 or check or digest is not None):
        sys.stderr.write("usage: ariva-age.py --recipient age1... [--check | --digest FILE] < plaintext > ciphertext\n")
        return 2
    if digest is not None and (not DIGEST_PATH.fullmatch(digest) or ".." in digest):
        refuse("--digest must be a plain absolute path")
    recipient = recipient_key(argv[1])
    if check:
        return 0
    sink = Counted(sys.stdout.buffer)
    try:
        encrypt(sys.stdin.buffer, sink, recipient)
        sys.stdout.buffer.flush()
        if digest is not None:
            descriptor = os.open(digest, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "w") as report:
                report.write(sink.hash.hexdigest() + " " + str(sink.length) + "\n")
    except (OSError, OverflowError) as error:
        sys.stderr.write("ariva-age: failed while writing: " + type(error).__name__ + "\n")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
