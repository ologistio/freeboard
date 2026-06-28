-- Authentication core schema.
-- Freeboard-generated ids are ULIDs stored as Crockford base32 CHAR(26) with
-- utf8mb4_bin (binary collation), matching the binary-collation id convention in
-- 001_initial_schema. Server-issued/verified secrets are stored as keyed HMAC-SHA256
-- digests in BINARY(32) columns; the raw secret is never stored.
-- schema_migrations is NOT created here; the migration runner bootstraps it.

CREATE TABLE IF NOT EXISTS users (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    email VARCHAR(190) NOT NULL,
    email_normalized VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    name VARCHAR(255) NOT NULL,
    global_role VARCHAR(32) NOT NULL,
    enabled TINYINT(1) NOT NULL DEFAULT 1,
    force_password_reset TINYINT(1) NOT NULL DEFAULT 0,
    mfa_enabled TINYINT(1) NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_users_email_normalized (email_normalized)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS user_password_credentials (
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    secret_version INT NOT NULL,
    -- Monotonic epoch bumped on every password change/reset. A session stores the
    -- credential_version it was issued under; the bearer handler rejects any session whose
    -- stored version is stale, so a password change invalidates ALL prior-version sessions
    -- race-free (even one a concurrent login inserted just after a revoke DELETE).
    credential_version INT NOT NULL DEFAULT 1,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (user_id),
    CONSTRAINT fk_user_password_credentials_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sessions (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    token_hash BINARY(32) NOT NULL,
    token_key_version INT NOT NULL,
    auth_state TINYINT NOT NULL,
    -- The credential epoch this session was issued under. Compared against the user's
    -- current user_password_credentials.credential_version on every bearer auth.
    credential_version INT NOT NULL,
    sudo_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_sessions_token_hash (token_hash),
    KEY ix_sessions_user_id (user_id),
    CONSTRAINT fk_sessions_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS password_reset_tokens (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    token_hash BINARY(32) NOT NULL,
    token_key_version INT NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    used_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_password_reset_tokens_token_hash (token_hash),
    KEY ix_password_reset_tokens_user_id (user_id),
    CONSTRAINT fk_password_reset_tokens_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS auth_rate_limits (
    bucket_kind VARCHAR(16) NOT NULL,
    bucket_key VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    attempt_count INT NOT NULL DEFAULT 0,
    window_started_at DATETIME(6) NOT NULL,
    locked_until DATETIME(6) NULL,
    PRIMARY KEY (bucket_kind, bucket_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS mfa_login_challenges (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    challenge_token_hash BINARY(32) NOT NULL,
    token_key_version INT NOT NULL,
    user_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    -- The credential epoch VERIFIED at the password step. The MFA completion re-reads the
    -- user's current epoch and rejects the challenge if it changed mid-flow, then issues the
    -- session under THIS verified epoch (not the current one read at issue time).
    credential_version INT NOT NULL,
    factors VARCHAR(64) NOT NULL,
    webauthn_options JSON NULL,
    magic_link_token_hash BINARY(32) NULL,
    magic_link_token_key_version INT NULL,
    magic_link_expires_at DATETIME(6) NULL,
    magic_link_sends INT NOT NULL DEFAULT 0,
    expires_at DATETIME(6) NOT NULL,
    consumed_at DATETIME(6) NULL,
    attempts INT NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_mfa_login_challenges_challenge_token_hash (challenge_token_hash),
    KEY ix_mfa_login_challenges_user_id (user_id),
    CONSTRAINT fk_mfa_login_challenges_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bootstrap_marker (
    id TINYINT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
