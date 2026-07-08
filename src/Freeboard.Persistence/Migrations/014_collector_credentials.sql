-- Per-collector machine credential for the runtime Evidence ingest route. Additive and forward-only;
-- alters no existing table. Ids use CHAR(26) ULID; the collector_id snapshot column uses utf8mb4_bin
-- to match Core's exact-byte id identity and the evidence_collectors id column it references.
--
-- collector_credentials foreign-keys evidence_collectors ON DELETE CASCADE: a credential is live config,
-- not history, so removing it with its collector is correct. Only the keyed HMAC (token_hash) and its
-- key version are stored; the raw token is never persisted.

CREATE TABLE IF NOT EXISTS collector_credentials (
    id CHAR(26) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    collector_id VARCHAR(190) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    token_hash BINARY(32) NOT NULL,
    token_key_version INT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    last_seen_at DATETIME(6) NULL,
    expires_at DATETIME(6) NULL,
    revoked_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY ux_collector_credentials_token_hash (token_hash),
    KEY ix_collector_credentials_collector_id (collector_id),
    CONSTRAINT fk_collector_credentials_collector
        FOREIGN KEY (collector_id) REFERENCES evidence_collectors (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
