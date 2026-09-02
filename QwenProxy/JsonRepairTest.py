"""
AES-256-CBC encrypt/decrypt helper — mirrors the C# TokenDecryptionMiddleware.

Algorithm: AES-256-CBC with PKCS7 padding.
Encoding:  plaintext is UTF-8 JSON; ciphertext is Base64.

Requirements:
    pip install pycryptodome

Usage:
    python JsonRepairTest.py
"""

import base64
import json
import os

from Crypto.Cipher import AES
from Crypto.Util.Padding import pad, unpad

# ---------------------------------------------------------------------------
# Configuration — must match appsettings.json > TokenEncryption
# ---------------------------------------------------------------------------
# 32-byte key (Base64-encoded in config)
AES_KEY_B64 = "T8kZLR4OsUC92Bv7SkibEUSYHXdhQ+JAXseP5tHypQ0="
# 16-byte IV (Base64-encoded in config)
AES_IV_B64 = "+andwrOoiwPcpltvfAActg=="


def _get_key_iv(key_base,Iv_base) -> tuple[bytes, bytes]:
    key = base64.b64decode(key_base)
    iv = base64.b64decode(Iv_base)
    assert len(key) == 32, f"Key must be 32 bytes, got {len(key)}"
    assert len(iv) == 16, f"IV must be 16 bytes, got {len(iv)}"
    return key, iv


def encrypt_token(data: dict,key_base,Iv_base) -> str:
    """
    Encrypt a dict (TokenClass payload) to a Base64 AES-256-CBC string.
    This is what the client sends as 'Authorization: Bearer <result>'.
    """
    key, iv = _get_key_iv(key_base, Iv_base)
    plaintext = json.dumps(data, separators=(",", ":")).encode("utf-8")
    cipher = AES.new(key, AES.MODE_CBC, iv)
    ciphertext = cipher.encrypt(pad(plaintext, AES.block_size))
    return base64.b64encode(ciphertext).decode("utf-8")


def decrypt_token(encrypted_b64: str,key_base,Iv_base) -> dict:
    """
    Decrypt a Base64 AES-256-CBC string back to a dict (TokenClass payload).
    Mirrors the C# middleware's DecryptAes + JSON deserialization.
    """
    key, iv = _get_key_iv(key_base, Iv_base)
    ciphertext = base64.b64decode(encrypted_b64)
    cipher = AES.new(key, AES.MODE_CBC, iv)
    plaintext = unpad(cipher.decrypt(ciphertext), AES.block_size)
    return json.loads(plaintext.decode("utf-8"))


# ---------------------------------------------------------------------------
# Demo / self-test
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    print(f"Key (Base64): {AES_KEY_B64}")
    print(f"IV  (Base64): {AES_IV_B64}")
    print()

    # The payload matching TokenClass { token, s5_token }
    payload = {
        "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpZCI6IjIzZTBkZjY5LWVkMTQtNDkwZS1hNmRkLTM3NjE1ZDM2MjM1ZCIsImxhc3RfcGFzc3dvcmRfY2hhbmdlIjoxNzg2NzczODU2LCJleHAiOjE3ODkzNjU4NTZ9.GZvBL6rOWPFxghYEp2nbw5bMBKXfeCpx1ck3xI0Cezg",
        "s5_token": "s5-secret-xyz789",
    }

    # Encrypt
    encrypted = encrypt_token(payload, AES_KEY_B64, AES_IV_B64)
    print(f"Encrypted (send as Bearer): {encrypted}")
    print()

    # Decrypt
    decrypted = decrypt_token(encrypted, AES_KEY_B64, AES_IV_B64)
    print(f"Decrypted: {json.dumps(decrypted, indent=2)}")
    print()

    # Verify round-trip
    assert decrypted["token"] == payload["token"]
    assert decrypted["s5_token"] == payload["s5_token"]
    print("[OK] Round-trip encryption/decryption successful.")
