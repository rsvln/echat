DOMAIN = "echat_notify"
DOMAIN_FULL = "echat_notify"

CONF_BOT_EMAIL = "bot_email"
CONF_BOT_PASSWORD = "bot_password"
CONF_IMAP_SERVER = "imap_server"
CONF_IMAP_PORT = "imap_port"
CONF_SMTP_SERVER = "smtp_server"
CONF_SMTP_PORT = "smtp_port"
CONF_USE_SSL = "use_ssl"
CONF_BOT_FINGERPRINT = "bot_fingerprint"

DEFAULT_IMAP_PORT = 993
DEFAULT_SMTP_PORT = 465
DEFAULT_SMTP_PORT_STARTTLS = 587

CHAT_VERSION = "2.0-batching"
AUTOCRYPT_HEADER = "Autocrypt"
CHAT_HEADERS = {
    "Chat-Version": CHAT_VERSION,
    "Chat-Message-ID": None,
    "Chat-Timestamp": None,
    "Chat-Disposition": "notification",
}

EMAIL_TEMPLATE = """From: {sender}
To: {recipient}
Subject: {subject}
{headers}

{body}"""

DEFAULT_SUBJECT = ""
