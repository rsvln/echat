import logging
from datetime import datetime
from typing import Optional, List

from homeassistant.components.notify import (
    BaseNotificationService,
    DOMAIN as NOTIFY_DOMAIN,
)
from homeassistant.const import CONF_EMAIL

from .client import EmailClient
from .crypto import CryptoService
from .keystore import Keystore
from .const import (
    CONF_BOT_EMAIL,
    CONF_BOT_PASSWORD,
    CONF_SMTP_SERVER,
    CONF_SMTP_PORT,
    CONF_USE_SSL,
)

_LOGGER = logging.getLogger(__name__)


class EchatNotificationService(BaseNotificationService):
    def __init__(self, hass, config: dict):
        self.hass = hass
        self.bot_email = config[CONF_BOT_EMAIL]
        self.bot_password = config[CONF_BOT_PASSWORD]
        self.smtp_server = config.get(CONF_SMTP_SERVER)
        self.smtp_port = config.get(CONF_SMTP_PORT) or 465
        self.use_ssl = config.get(CONF_USE_SSL, True)

        self.keystore = Keystore()
        self.crypto = CryptoService()

        self._bot_keys = self.keystore.get_bot_keys(self.bot_email)
        if not self._bot_keys:
            _LOGGER.warning(
                "Bot keys not found for %s. Run config flow to generate keys.",
                self.bot_email,
            )

        self._email_client = EmailClient(
            email=self.bot_email,
            password=self.bot_password,
            smtp_server=self.smtp_server,
            smtp_port=self.smtp_port,
            use_ssl=self.use_ssl,
        )

    @property
    def bot_public_key(self) -> Optional[str]:
        if self._bot_keys:
            return self._bot_keys["public_key"]
        return None

    @property
    def should_sign(self) -> bool:
        return self.bot_public_key is not None

    def send_message(self, message: str = "", **kwargs) -> None:
        target = kwargs.get("target")
        if not target:
            _LOGGER.error("No target specified")
            return

        recipients = target if isinstance(target, list) else [target]

        for recipient in recipients:
            self._send_to_recipient(recipient, message)

    def _send_to_recipient(self, recipient: str, message: str) -> None:
        try:
            contact = self.keystore.get_contact_key(recipient)

            body = message
            autocrypt_header = None

            if contact and contact.public_key:
                try:
                    body = self.crypto.encrypt(message, contact.public_key)
                    _LOGGER.debug("Message encrypted for %s", recipient)
                except Exception as e:
                    _LOGGER.warning(
                        "Encryption failed for %s, sending plaintext: %s",
                        recipient,
                        e,
                    )
            else:
                _LOGGER.debug(
                    "No public key for %s, sending plaintext with Autocrypt header",
                    recipient,
                )

            if self.bot_public_key:
                autocrypt_header = CryptoService.build_autocrypt_header(
                    self.bot_email, self.bot_public_key
                )

            timestamp = datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ")

            self._email_client.send_message(
                recipient=recipient,
                body=body,
                autocrypt_header=autocrypt_header,
                timestamp=timestamp,
            )

            _LOGGER.info("Message sent to %s", recipient)

        except Exception as e:
            _LOGGER.error("Failed to send message to %s: %s", recipient, e)
            raise


def get_service(hass, config: dict, target: Optional[List[str]] = None) -> Optional[EchatNotificationService]:
    if not config:
        return None
    return EchatNotificationService(hass, config)
