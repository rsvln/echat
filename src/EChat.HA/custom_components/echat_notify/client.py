import smtplib
import uuid
from email.mime.text import MIMEText
from email.mime.multipart import MIMEMultipart
from email.header import Header
from typing import Optional
from .const import CHAT_VERSION


class EmailClient:
    def __init__(
        self,
        email: str,
        password: str,
        smtp_server: str,
        smtp_port: int,
        use_ssl: bool = True,
    ):
        self.email = email
        self.password = password
        self.smtp_server = smtp_server
        self.smtp_port = smtp_port
        self.use_ssl = use_ssl

    def _create_message_id(self) -> str:
        return f"<{uuid.uuid4().hex}@{self.email}>"

    def _get_smtp(self) -> smtplib.SMTP:
        if self.use_ssl:
            smtp = smtplib.SMTP_SSL(self.smtp_server, self.smtp_port)
        else:
            smtp = smtplib.SMTP(self.smtp_server, self.smtp_port)
            smtp.starttls()
        smtp.login(self.email, self.password)
        return smtp

    def send_message(
        self,
        recipient: str,
        body: str,
        autocrypt_header: Optional[str] = None,
        subject: str = "",
        chat_message_id: Optional[str] = None,
        timestamp: Optional[str] = None,
    ) -> bool:
        msg = MIMEMultipart("mixed")
        msg["From"] = self.email
        msg["To"] = recipient
        msg["Subject"] = subject or Header("", charset="utf-8")

        headers = {
            "Chat-Version": CHAT_VERSION,
            "Chat-Message-ID": chat_message_id or self._create_message_id(),
            "Chat-Timestamp": timestamp or "",
            "Chat-Disposition": "inline",
        }

        if autocrypt_header:
            headers["Autocrypt"] = autocrypt_header

        for key, value in headers.items():
            if value:
                msg[key] = value

        body_part = MIMEText(body, "plain", charset="utf-8")
        msg.attach(body_part)

        try:
            with self._get_smtp() as smtp:
                smtp.sendmail(self.email, [recipient], msg.as_string())
            return True
        except Exception as e:
            raise Exception(f"Failed to send message: {e}")

    def test_connection(self) -> tuple[bool, str]:
        try:
            with self._get_smtp() as smtp:
                pass
            return True, "Connection successful"
        except Exception as e:
            return False, str(e)


def detect_smtp_settings(email: str) -> tuple[str, int, bool]:
    domain = email.lower().split("@")[1] if "@" in email else ""

    providers = {
        "gmail.com": ("smtp.gmail.com", 465, True),
        "yandex.ru": ("smtp.yandex.ru", 465, True),
        "yandex.com": ("smtp.yandex.ru", 465, True),
        "mail.ru": ("smtp.mail.ru", 465, True),
        "outlook.com": ("smtp-mail.outlook.com", 587, False),
        "hotmail.com": ("smtp-mail.outlook.com", 587, False),
        "icloud.com": ("smtp.mail.icloud.com", 587, False),
    }

    if domain in providers:
        return providers[domain]

    if "yandex" in domain:
        return ("smtp.yandex.ru", 465, True)
    if "google" in domain or "gmail" in domain:
        return ("smtp.gmail.com", 465, True)

    raise ValueError(f"Cannot auto-detect SMTP settings for {domain}")
