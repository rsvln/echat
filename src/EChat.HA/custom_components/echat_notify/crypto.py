import gnupg
import os
import hashlib
import re
from typing import Optional, Tuple
from dataclasses import dataclass

GPG_HOME = os.path.join(os.path.dirname(__file__), ".gnupg")


@dataclass
class KeyPair:
    public_key: str
    fingerprint: str


class CryptoService:
    def __init__(self, gpg_home: str = GPG_HOME):
        self.gpg_home = gpg_home
        os.makedirs(self.gpg_home, exist_ok=True)
        self.gpg = gnupg.GPG(gnupghome=self.gpg_home, keyring=None, secring=None)

    def generate_keypair(self, email: str, passphrase: Optional[str] = None) -> KeyPair:
        input_data = self.gpg.gen_key_input(
            key_type="RSA",
            key_length=2048,
            name_real="eChat Bot",
            name_email=email,
            passphrase=passphrase or "",
            expire_date="0",
        )
        key = self.gpg.gen_key(input_data)
        if not key.fingerprint:
            raise Exception("Failed to generate keypair")

        public_key = self.export_public_key(key.fingerprint)
        fingerprint = self.get_fingerprint(public_key)

        return KeyPair(public_key=public_key, fingerprint=fingerprint)

    def export_public_key(self, fingerprint: str) -> str:
        ascii_armored = self.gpg.export_keys(fingerprint, secret=False)
        return ascii_armored

    def get_fingerprint(self, public_key: str) -> str:
        key_data = public_key.strip()
        result = self.gpg.import_keys(key_data)
        if not result.fingerprints:
            raise Exception("Failed to import key for fingerprint extraction")
        imported_fp = result.fingerprints[0]
        return imported_fp.replace(" ", "").upper()

    def encrypt(self, message: str, public_key: str) -> str:
        key_data = public_key.strip()
        result = self.gpg.import_keys(key_data)
        if not result.fingerprints:
            raise Exception("Failed to import recipient key")
        recipient_fp = result.fingerprints[0]

        encrypted = self.gpg.encrypt(
            message,
            recipients=[recipient_fp],
            always_trust=True,
            add_expiry=False,
        )
        if not encrypted.ok:
            raise Exception(f"Encryption failed: {encrypted.status}")
        return str(encrypted)

    def decrypt(self, encrypted_data: str, passphrase: str) -> str:
        decrypted = self.gpg.decrypt(encrypted_data, passphrase=passphrase)
        if not decrypted.ok:
            raise Exception(f"Decryption failed: {decrypted.status}")
        return str(decrypted)

    @staticmethod
    def parse_autocrypt(header: str) -> Tuple[str, str]:
        addr = None
        keydata = None

        for part in header.split(";"):
            part = part.strip()
            if "=" in part:
                key, _, value = part.partition("=")
                key = key.strip().lower()
                value = value.strip()
                if key == "addr":
                    addr = value
                elif key == "keydata":
                    keydata = value

        if not addr or not keydata:
            raise ValueError(f"Invalid Autocrypt header: {header}")

        return addr, keydata

    @staticmethod
    def build_autocrypt_header(addr: str, public_key: str) -> str:
        keydata = public_key.strip()
        return f"addr={addr}; keydata={keydata}"
