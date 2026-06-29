## Context

The sudo step-up magic-link reuses the `mfa_login_challenges` row: there is one
active sudo challenge per user (a `(user_id, sudo_dedupe_key)` unique key,
migration 004), and the magic-link token lives in single columns
(`magic_link_token_hash`, `magic_link_token_key_version`, `magic_link_expires_at`)
with a `magic_link_sends` counter. Each send overwrites those columns, so only the
latest token verifies. `FindOrCreateSudoMagicLinkAsync` mints a send and reports
`Sent`; `VerifyAndConsumeMagicLinkAsync` (used ONLY by the sudo endpoint) checks
the column and consumes the challenge.

The login magic-link fallback uses different methods (`SetMagicLinkAsync`,
`VerifyMagicLinkAsync`) on the login challenge row and is out of scope.

## Decision: one token row per send

Add `mfa_sudo_magic_link_tokens` (child of `mfa_login_challenges`,
`ON DELETE CASCADE`): `id`, `challenge_id`, `token_hash` (unique), `token_key_version`,
`expires_at`, `consumed_at`, `created_at`. Each accepted sudo send inserts a row.
The token verifies for as long as it is unconsumed and unexpired and its challenge
is unconsumed and unexpired, so a second send never invalidates an earlier emailed
token. Verifying any token consumes the challenge, which is single-use, so only one
token can ever complete a given step-up.

The sudo challenge row keeps owning identity, user binding, expiry, consume, and
the dedupe key; it stops being the magic-link token store. `magic_link_sends` is no
longer used by the sudo path (the cap is now the count of active token rows). The
`magic_link_*` columns stay on the table because the login path still uses them.

## Atomicity and the re-send cap

`FindOrCreateSudoMagicLinkAsync` runs in one `ReadCommitted` transaction:

1. `INSERT ... ON DUPLICATE KEY UPDATE` find-or-create-or-reset the single sudo
   challenge row (the existing migration-004 dedupe), managing ONLY challenge
   identity/expiry/consume - no token columns. This locks the row, so concurrent
   sudo sends for one user serialise here.
2. Read the row id and `created_at`. `created_at == @now` means the row was just
   created or reset this call; in that case DELETE any existing token rows for the
   challenge so a fresh/reset challenge starts clean (defends a token outliving its
   challenge if `MagicLinkLifetime` were ever set above `MfaChallengeLifetime`;
   today they are equal).
3. COUNT active token rows (`consumed_at IS NULL AND expires_at > @now`). If
   `>= maxSends`, the cap is reached: `Sent = false`, insert nothing.
4. Otherwise INSERT one token row and `Sent = true`.

Because the challenge row is locked for the whole transaction, a concurrent send
blocks at step 1 until this one commits, so the count in step 3 is exact and the
cap cannot be multiplied by a race. Concurrent sends therefore each insert their
own token (up to the cap), and both verify.

## Verify

`VerifyAndConsumeMagicLinkAsync(id, userId, token, now)` (sudo): load the challenge
for `id AND user_id`; reject if missing, consumed, expired, or user-mismatched.
Select the challenge's active token rows (`consumed_at IS NULL AND expires_at > now`)
and HMAC-verify the presented prefixless token against each under its stored key
version (constant-time, a handful of rows at most). On a match, atomically consume
the CHALLENGE (`UPDATE ... SET consumed_at WHERE id AND user_id AND consumed_at IS NULL`);
only the call that flips it returns true, keeping single-use and user-binding. The
matched token row is also marked consumed for hygiene.

## Risks / Trade-offs

- [Multiple valid tokens at once] -> Intended: each emailed link works until used,
  expires, or the challenge is consumed. The challenge consume keeps the step-up
  single-use, so only one token can complete it.
- [Reset reuse] -> A reset challenge clears its tokens (step 2), and by default a
  token cannot outlive its challenge, so a prior attempt's token cannot complete a
  new step-up.
- [Slightly more work per verify] -> Up to `maxSends` HMAC comparisons (a small
  cap). Negligible.
- [Unused `magic_link_*` columns for sudo rows] -> Left in place because the login
  path still uses them; dropping them is a separate change.

## Verification

- A store-level test: two sequential sends under the cap both produce tokens that
  each verify (until one consumes the challenge); a third send past the cap returns
  `Sent = false`; an expired or consumed challenge rejects.
- A gated MySQL integration test mirrors the original concurrency scenario: two
  sends, BOTH emitted tokens verify (the older is not clobbered), and consuming via
  one makes the challenge single-use.
- Web unit tests drive `/auth/sudo` through the updated `FakeMfaStores`.
