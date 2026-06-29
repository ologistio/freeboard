## 1. Schema

<!-- commit: feat(auth): add per-send sudo magic-link token table -->

- [x] 1.1 Add `src/Freeboard.Persistence/Migrations/006_sudo_magic_link_tokens.sql`:
      `mfa_sudo_magic_link_tokens` (id, challenge_id, token_hash unique,
      token_key_version, expires_at, consumed_at, created_at), `challenge_id` FK to
      `mfa_login_challenges(id)` `ON DELETE CASCADE`, index on `challenge_id`.

## 2. Store

<!-- commit: fix(auth): store each sudo magic-link send as its own verifiable token -->

- [x] 2.1 Rewrite `MySqlMfaChallengeStore.FindOrCreateSudoMagicLinkAsync` to run in
      one ReadCommitted transaction: find-or-create-or-reset the sudo challenge row
      (identity/expiry/consume only, keeping the migration-004 dedupe); on a fresh
      or reset row (created_at == now) delete the challenge's existing token rows;
      count active token rows; insert one token row and return Sent=true when under
      `maxSends`, else Sent=false. Stop writing the `magic_link_*` columns here.
- [x] 2.2 Rewrite `VerifyAndConsumeMagicLinkAsync` (sudo) to match the presented
      prefixless token against the challenge's active token rows (HMAC under each
      stored key version), then atomically consume the challenge bound to the user;
      mark the matched token consumed. Leave `VerifyMagicLinkAsync` / the login path
      unchanged.

## 3. Tests

<!-- commit: test(auth): cover per-send sudo magic-link tokens -->

- [x] 3.1 Update `tests/Freeboard.Web.Tests/FakeMfaStores.cs` sudo methods to mirror
      the per-send token model (per-challenge token list, cap by active count,
      reset clears tokens, verify matches any active token + consumes the challenge).
- [x] 3.2 Add a gated MySQL integration test: two sudo sends under the cap both
      produce tokens that each verify; consuming via one makes the challenge
      single-use; a send past the cap returns Sent=false; expired/consumed and
      cross-user are rejected.
- [x] 3.3 Keep the existing sudo web tests green; add/adjust a web test for the
      "two sends, both links work" behaviour via the fake.

## 4. Verify

<!-- commit: (verification only - no code) -->

- [x] 4.1 `dotnet build` clean; `dotnet test` green with the MySQL tests skipping
      when `FREEBOARD_TEST_DB` is unset; run the integration test against a real DB.
- [x] 4.2 `openspec validate "fix-sudo-magic-link-per-send-tokens"` passes.
