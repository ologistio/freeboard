-- Preserve a confirmed TOTP secret while rotating to a new one. Enrolling a replacement secret
-- must not destroy the working secret before the new one is proven: otherwise an abandoned
-- rotation leaves users.mfa_enabled = true with no usable TOTP secret, locking the user out of
-- the factor. The new secret is staged in these pending_* columns and promoted to the live
-- columns only when activation verifies a code computed from it. Same at-rest encryption shape as
-- the live columns (AES-256-GCM ciphertext/nonce/tag/version).

ALTER TABLE totp_credentials
    ADD COLUMN pending_secret_ciphertext VARBINARY(255) NULL,
    ADD COLUMN pending_secret_nonce VARBINARY(12) NULL,
    ADD COLUMN pending_secret_tag VARBINARY(16) NULL,
    ADD COLUMN pending_key_version INT NULL,
    ADD COLUMN pending_created_at DATETIME(6) NULL;
