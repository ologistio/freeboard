-- MFA schema. Same conventions as 002_auth_core: CHAR(26) utf8mb4_bin ULID ids,
-- FK ON DELETE CASCADE, explicit KEY (user_id), keyed-HMAC digests in BINARY(32).
-- TOTP secrets are encrypted at rest (AES-256-GCM) with the ciphertext/nonce/tag/version
-- stored. aaguid stays CHAR(36): it is a vendor-assigned UUID, not Freeboard-generated.

CREATE TABLE IF NOT EXISTS webauthn_credentials (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    credential_id VARBINARY(255) NOT NULL,
    public_key VARBINARY(1024) NOT NULL,
    sign_count BIGINT UNSIGNED NOT NULL,
    user_handle VARBINARY(64) NOT NULL,
    aaguid CHAR(36) NULL,
    transports VARCHAR(255) NULL,
    cred_type VARCHAR(32) NULL,
    is_backup_eligible TINYINT(1) NULL,
    is_backed_up TINYINT(1) NULL,
    nickname VARCHAR(190) NULL,
    created_at DATETIME(6) NOT NULL,
    last_used_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_webauthn_credentials_credential_id (credential_id),
    KEY ix_webauthn_credentials_user_id (user_id),
    CONSTRAINT fk_webauthn_credentials_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS totp_credentials (
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    secret_ciphertext VARBINARY(255) NOT NULL,
    secret_nonce VARBINARY(12) NOT NULL,
    secret_tag VARBINARY(16) NOT NULL,
    key_version INT NOT NULL,
    confirmed_at DATETIME(6) NULL,
    last_time_step BIGINT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (user_id),
    CONSTRAINT fk_totp_credentials_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS mfa_recovery_codes (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    code_hash BINARY(32) NOT NULL,
    token_key_version INT NOT NULL,
    used_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_mfa_recovery_codes_user_id (user_id),
    CONSTRAINT fk_mfa_recovery_codes_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
