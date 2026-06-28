-- Make "find-or-create the active sudo magic-link challenge" atomic per user.
-- A nullable discriminator plus a (user_id, sudo_dedupe_key) unique key means at most one
-- active sudo magic-link challenge can exist per user. NULL keys do not collide in a MySQL
-- unique index, so ordinary login challenges (which leave this NULL) are unaffected.
--
-- FindOrCreateSudoMagicLinkAsync uses INSERT ... ON DUPLICATE KEY UPDATE against this key so
-- the row is created-or-reused and magic_link_sends is incremented in one atomic statement.
-- Concurrent first sends therefore converge on a single row and the per-challenge re-send cap
-- holds instead of being multiplied by a race.
--
-- A consumed or expired sudo row keeps its key; the next send RESETS that row in place (new
-- challenge token, sends back to 1, fresh expiry, consumed_at cleared) rather than inserting a
-- second row, so the one-row-per-user invariant holds across the challenge lifecycle.

ALTER TABLE mfa_login_challenges
    ADD COLUMN sudo_dedupe_key VARCHAR(64) NULL,
    ADD UNIQUE KEY uq_mfa_login_challenges_user_sudo_dedupe (user_id, sudo_dedupe_key);
