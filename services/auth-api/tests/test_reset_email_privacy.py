"""Privacy contracts for password-reset delivery logs."""

from unittest.mock import MagicMock, patch

from main import _bootstrap_admin, _send_reset_email


def _render_calls(mock_logger: MagicMock) -> str:
    return repr(mock_logger.method_calls)


def test_missing_smtp_does_not_log_recipient_or_reset_token():
    email = "private.user@example.test"
    token = "PRIVATE-RESET-TOKEN-DO-NOT-LOG"
    mock_logger = MagicMock()

    with patch.dict("main.os.environ", {}, clear=True), patch("main.logger", mock_logger):
        _send_reset_email(email, token)

    rendered = _render_calls(mock_logger)
    assert email not in rendered
    assert token not in rendered
    mock_logger.warning.assert_called_once_with(
        "Password reset email was not sent because SMTP is not configured"
    )


def test_smtp_failure_logs_only_exception_type():
    email = "private.user@example.test"
    token = "PRIVATE-RESET-TOKEN-DO-NOT-LOG"
    secret_error = "SMTP rejected private.user@example.test with PRIVATE-SERVER-DETAIL"
    mock_logger = MagicMock()

    with (
        patch.dict("main.os.environ", {"SMTP_HOST": "smtp.example.test"}, clear=True),
        patch("main.smtplib.SMTP", side_effect=RuntimeError(secret_error)),
        patch("main.logger", mock_logger),
    ):
        _send_reset_email(email, token)

    rendered = _render_calls(mock_logger)
    assert email not in rendered
    assert token not in rendered
    assert secret_error not in rendered
    assert "PRIVATE-SERVER-DETAIL" not in rendered
    mock_logger.warning.assert_called_once_with(
        "Password reset email delivery failed error_type=%s",
        "RuntimeError",
    )


def test_bootstrap_admin_log_does_not_include_email_or_password():
    email = "private.admin@example.test"
    password = "PRIVATE-BOOTSTRAP-PASSWORD"
    mock_logger = MagicMock()

    with (
        patch.dict(
            "main.os.environ",
            {"BOOTSTRAP_ADMIN_EMAIL": email, "BOOTSTRAP_ADMIN_PASSWORD": password},
            clear=True,
        ),
        patch("main.get_user_by_email", return_value=None),
        patch("main.create_user"),
        patch("main.hash_password", return_value="safe-hash"),
        patch("main.logger", mock_logger),
    ):
        _bootstrap_admin()

    rendered = _render_calls(mock_logger)
    assert email not in rendered
    assert password not in rendered
    mock_logger.info.assert_called_once_with("Bootstrap admin user created")
