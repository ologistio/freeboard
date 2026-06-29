## Why

The sudo step-up magic-link stores its token in a single set of columns on the
challenge row (`magic_link_token_hash` and friends). Every send overwrites that
slot, so only the most recent token is ever valid. A second send - concurrent or
sequential - invalidates the link that an earlier send already emailed, so a user
can receive a sudo magic-link email whose link no longer verifies.

This was harmless while no email transport shipped. Now that mail is delivered, a
delivered sudo magic-link can fail to verify, which is a real (if narrow)
reliability bug for the step-up flow.

## What Changes

- Add a `mfa_sudo_magic_link_tokens` table: one row per sudo magic-link send,
  each with its own keyed-HMAC token hash, key version, and expiry.
- `FindOrCreateSudoMagicLinkAsync` records each accepted send as its OWN token row
  instead of overwriting a column, so concurrent and repeated sends each yield a
  token that verifies independently. The per-challenge re-send cap becomes the
  count of active (unconsumed, unexpired) token rows, enforced atomically.
- `VerifyAndConsumeMagicLinkAsync` (sudo) matches the presented token against ANY
  active token row for the challenge and the bound user, then consumes the
  challenge (single-use) as before.
- A fresh or reset sudo challenge starts with no tokens (old rows are cleared), so
  a token from a prior step-up attempt cannot verify a new one.

Scope is the sudo magic-link path only. The MFA-login magic-link fallback
(`SetMagicLinkAsync` / `VerifyMagicLinkAsync`) is unchanged. The
`IMfaChallengeStore` method signatures are unchanged; only the storage and the
two sudo method bodies change.

## Capabilities

### Modified Capabilities

- `mfa`: the sudo magic-link send/verify behaviour is refined so each send's token
  is independently verifiable until it is used, expires, or the challenge is
  consumed - removing the last-writer-wins clobbering of an already-emailed link.

## Impact

- New: `src/Freeboard.Persistence/Migrations/006_sudo_magic_link_tokens.sql`.
- Modified: `MySqlMfaChallengeStore.FindOrCreateSudoMagicLinkAsync` and
  `VerifyAndConsumeMagicLinkAsync`; the test fake `FakeMfaStores` (sudo methods);
  added store-level and gated integration tests.
- No API, config, or interface-signature change. The MFA-login magic-link path and
  all other auth flows are untouched. The now-unused single-slot magic-link columns
  remain on the challenge row for the login path, which still uses them.

## Non-goals

- No change to the MFA-login magic-link fallback or its single-slot storage.
- No change to the sudo factor set, rate limiting, or the `/auth/sudo` API.
- No migration that drops the existing `magic_link_*` columns (the login path
  still uses them).
