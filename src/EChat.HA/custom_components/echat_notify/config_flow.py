import voluptuous as vol
from homeassistant import config_entries

from .const import (
    DOMAIN,
    CONF_BOT_EMAIL,
    CONF_BOT_PASSWORD,
    CONF_SMTP_SERVER,
    CONF_SMTP_PORT,
    CONF_USE_SSL,
)
from .client import detect_smtp_settings

CONFIG_SCHEMA = vol.Schema(
    {
        vol.Required(CONF_BOT_EMAIL): str,
        vol.Required(CONF_BOT_PASSWORD): str,
        vol.Optional(CONF_SMTP_SERVER): str,
        vol.Optional(CONF_SMTP_PORT): int,
        vol.Optional(CONF_USE_SSL, default=True): bool,
    }
)


class EchatConfigFlow(config_entries.ConfigFlow, domain=DOMAIN):
    VERSION = 1

    async def async_step_user(self, user_input=None):
        errors = {}

        if user_input is not None:
            email = user_input[CONF_BOT_EMAIL]
            password = user_input[CONF_BOT_PASSWORD]
            smtp_server = user_input.get(CONF_SMTP_SERVER)
            smtp_port = user_input.get(CONF_SMTP_PORT)
            use_ssl = user_input.get(CONF_USE_SSL, True)

            try:
                if not smtp_server or not smtp_port:
                    smtp_server, smtp_port, use_ssl = detect_smtp_settings(email)

                result = await self.hass.async_add_executor_job(
                    self._setup_keys_and_test, email, password, smtp_server, smtp_port, use_ssl
                )
                if not result["success"]:
                    errors["base"] = result.get("error", "auth_failed")

            except Exception as e:
                errors["base"] = str(e)

            if not errors:
                return self.async_create_entry(
                    title=f"eChat Bot ({email})",
                    data={
                        CONF_BOT_EMAIL: email,
                        CONF_BOT_PASSWORD: password,
                        CONF_SMTP_SERVER: smtp_server,
                        CONF_SMTP_PORT: smtp_port,
                        CONF_USE_SSL: use_ssl,
                    },
                )

        return self.async_show_form(
            step_id="user",
            data_schema=CONFIG_SCHEMA,
            errors=errors,
        )

    def _setup_keys_and_test(self, email, password, smtp_server, smtp_port, use_ssl):
        from .crypto import CryptoService
        from .keystore import Keystore
        from .client import EmailClient

        crypto = CryptoService()
        keypair = crypto.generate_keypair(email)

        keystore = Keystore()
        keystore.save_bot_keys(
            email=email,
            public_key=keypair.public_key,
            private_key="",
            fingerprint=keypair.fingerprint,
        )

        client = EmailClient(
            email=email,
            password=password,
            smtp_server=smtp_server,
            smtp_port=smtp_port,
            use_ssl=use_ssl,
        )
        success, message = client.test_connection()
        if not success:
            return {"success": False, "error": message}

        return {"success": True}
