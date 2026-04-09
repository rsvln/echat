import sqlite3
import os
import time
from typing import Optional
from dataclasses import dataclass
from datetime import datetime

DATABASE_PATH = os.path.join(os.path.dirname(__file__), "echat_keystore.db")


@dataclass
class ContactKey:
    email: str
    public_key: Optional[str]
    fingerprint: Optional[str]
    verified: bool
    updated_at: float


class Keystore:
    def __init__(self, db_path: str = DATABASE_PATH):
        self.db_path = db_path
        self._init_db()

    def _get_conn(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        return conn

    def _init_db(self):
        with self._get_conn() as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS bot_keys (
                    email TEXT PRIMARY KEY,
                    public_key TEXT NOT NULL,
                    private_key TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,
                    created_at REAL NOT NULL
                )
            """)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS contacts (
                    email TEXT PRIMARY KEY,
                    public_key TEXT,
                    fingerprint TEXT,
                    verified INTEGER DEFAULT 0,
                    updated_at REAL NOT NULL
                )
            """)
            conn.commit()

    def save_bot_keys(self, email: str, public_key: str, private_key: str, fingerprint: str):
        with self._get_conn() as conn:
            conn.execute("""
                INSERT OR REPLACE INTO bot_keys (email, public_key, private_key, fingerprint, created_at)
                VALUES (?, ?, ?, ?, ?)
            """, (email, public_key, private_key, fingerprint, time.time()))
            conn.commit()

    def get_bot_keys(self, email: str) -> Optional[dict]:
        with self._get_conn() as conn:
            row = conn.execute(
                "SELECT * FROM bot_keys WHERE email = ?", (email,)
            ).fetchone()
            if row:
                return dict(row)
            return None

    def save_contact_key(self, email: str, public_key: str, fingerprint: Optional[str] = None, verified: bool = False):
        with self._get_conn() as conn:
            conn.execute("""
                INSERT OR REPLACE INTO contacts (email, public_key, fingerprint, verified, updated_at)
                VALUES (?, ?, ?, ?, ?)
            """, (email, public_key, fingerprint, 1 if verified else 0, time.time()))
            conn.commit()

    def get_contact_key(self, email: str) -> Optional[ContactKey]:
        with self._get_conn() as conn:
            row = conn.execute(
                "SELECT * FROM contacts WHERE email = ?", (email,)
            ).fetchone()
            if row:
                return ContactKey(
                    email=row["email"],
                    public_key=row["public_key"],
                    fingerprint=row["fingerprint"],
                    verified=bool(row["verified"]),
                    updated_at=row["updated_at"],
                )
            return None

    def get_all_contacts(self) -> list[ContactKey]:
        with self._get_conn() as conn:
            rows = conn.execute("SELECT * FROM contacts").fetchall()
            return [
                ContactKey(
                    email=row["email"],
                    public_key=row["public_key"],
                    fingerprint=row["fingerprint"],
                    verified=bool(row["verified"]),
                    updated_at=row["updated_at"],
                )
                for row in rows
            ]

    def delete_contact(self, email: str):
        with self._get_conn() as conn:
            conn.execute("DELETE FROM contacts WHERE email = ?", (email,))
            conn.commit()
