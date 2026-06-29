-- Per-send sudo magic-link tokens. Each accepted sudo magic-link send stores its own token row
-- instead of overwriting a single column on the challenge, so concurrent or repeated sends each
-- yield a token that verifies independently - a later send no longer clobbers an already-emailed
-- link. The per-challenge re-send cap is the count of active (unconsumed, unexpired) token rows.
--
-- Scoped to the sudo step-up magic-link. The MFA-login magic-link fallback keeps using the single
-- magic_link_* columns on mfa_login_challenges. Rows cascade-delete with their challenge.

-- challenge_created_at binds a token to ONE challenge instance: a sudo challenge row is reset in
-- place (its created_at is bumped) when its prior instance expired or was consumed, so tokens from
-- the prior instance keep the old created_at and no longer match the current one. No equality
-- against an application clock is needed; both sides are read back from the row.
CREATE TABLE IF NOT EXISTS mfa_sudo_magic_link_tokens (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    challenge_id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    challenge_created_at DATETIME(6) NOT NULL,
    token_hash BINARY(32) NOT NULL,
    token_key_version INT NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    consumed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_mfa_sudo_magic_link_tokens_hash (token_hash),
    KEY ix_mfa_sudo_magic_link_tokens_challenge (challenge_id, challenge_created_at),
    CONSTRAINT fk_mfa_sudo_magic_link_tokens_challenge
        FOREIGN KEY (challenge_id) REFERENCES mfa_login_challenges (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
