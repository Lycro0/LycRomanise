"""Request encryption for NetEase Cloud Music's web APIs (weapi and eapi).

The plain, unauthenticated lyric endpoint does not include word-by-word
("yrc") lyrics. NetEase's own web player and desktop client ask for lyrics
through *encrypted* endpoints instead, and those answer with the "yrc" field
for songs that have it:

  weapi  (https://music.163.com/weapi/...)
         JSON -> AES-128-CBC (fixed key) -> base64 -> AES-128-CBC (random
         16-char key) -> base64; the random key is sent RSA-encrypted
         ("encSecKey"). All constants below are the public ones every
         NetEase client uses; they are not secrets.
  eapi   (https://interface.music.163.com/eapi/...)
         "<path>-36cd479b6b5-<json>-36cd479b6b5-<md5>" -> AES-128-ECB.

Needs the `cryptography` package. If it is missing, available() is False and
the caller simply skips the encrypted attempts (nothing else breaks).
"""

import base64
import hashlib
import json
import os
import secrets

try:
    from cryptography.hazmat.primitives import padding
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
except Exception as _exc:          # not installed: word-timed NetEase lyrics just stay unavailable
    Cipher = None
    IMPORT_ERROR = _exc
else:
    IMPORT_ERROR = None

PRESET_KEY = b"0CoJUm6Qyw8W8jud"
IV = b"0102030405060708"
EAPI_KEY = b"e82ckenh8dichen8"
BASE62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
RSA_MODULUS = int(
    "e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec"
    "4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d8"
    "13cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7", 16)
RSA_EXPONENT = 0x10001


def available():
    return Cipher is not None


def _pkcs7(data):
    p = padding.PKCS7(128).padder()
    return p.update(data) + p.finalize()


def _cbc(data, key, iv=IV):
    enc = Cipher(algorithms.AES(key), modes.CBC(iv)).encryptor()
    return enc.update(_pkcs7(data)) + enc.finalize()


def _ecb(data, key=EAPI_KEY):
    enc = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
    return enc.update(_pkcs7(data)) + enc.finalize()


def _rsa(secret_key):
    """Textbook RSA (no padding) of the *reversed* secret key, as a 256-digit hex string."""
    n = int.from_bytes(secret_key[::-1], "big")
    return format(pow(n, RSA_EXPONENT, RSA_MODULUS), "x").zfill(256)


def weapi_encrypt(payload, secret_key=None):
    """dict -> the two form fields weapi wants: {"params", "encSecKey"}.
    `secret_key` (16 bytes) is random unless a test pins it."""
    if secret_key is None:
        secret_key = "".join(secrets.choice(BASE62) for _ in range(16)).encode()
    text = json.dumps(payload, separators=(",", ":")).encode()
    first = base64.b64encode(_cbc(text, PRESET_KEY))
    params = base64.b64encode(_cbc(first, secret_key)).decode()
    return {"params": params, "encSecKey": _rsa(secret_key)}


def eapi_encrypt(path, payload):
    """`path` is the API path as eapi signs it (e.g. "/api/song/lyric/v1"). -> {"params": HEX}."""
    text = json.dumps(payload, separators=(",", ":"))
    digest = hashlib.md5(("nobody%suse%smd5forencrypt" % (path, text)).encode()).hexdigest()
    message = "%s-36cd479b6b5-%s-36cd479b6b5-%s" % (path, text, digest)
    return {"params": _ecb(message.encode()).hex().upper()}


def eapi_decrypt(hex_text):
    """Inverse of the eapi body encryption (used by tests; eapi answers are plain JSON)."""
    dec = Cipher(algorithms.AES(EAPI_KEY), modes.ECB()).decryptor()
    raw = dec.update(bytes.fromhex(hex_text)) + dec.finalize()
    un = padding.PKCS7(128).unpadder()
    return (un.update(raw) + un.finalize()).decode()
