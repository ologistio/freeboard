## ADDED Requirements

### Requirement: Sudo magic-link sends are independently verifiable

Each accepted sudo magic-link send SHALL store its own single-use, keyed-HMAC
token (with its own key version and expiry) rather than overwriting one shared
token slot on the challenge. A token SHALL verify for as long as it is unconsumed
and unexpired and its challenge is unconsumed and unexpired, so a later send (or a
concurrent send) SHALL NOT invalidate a token that an earlier send already emailed.
The per-challenge re-send cap SHALL be the count of active (unconsumed, unexpired)
tokens for the challenge, enforced atomically so concurrent sends cannot multiply
it. Verifying any one of the challenge's tokens SHALL consume the challenge, which
remains single-use and bound to the user, so at most one token can complete a given
step-up. A freshly created or reset sudo challenge SHALL start with no tokens, so a
token issued for a prior step-up attempt SHALL NOT verify a new one.

#### Scenario: Two sends both verify (no clobbering)

- **WHEN** a user requests a sudo magic-link twice (sequentially or concurrently)
  while under the re-send cap
- **THEN** each send emails a token, and EITHER emailed token verifies the step-up
- **AND** completing the step-up with one token consumes the challenge, so the
  other token can no longer be used

#### Scenario: Re-send cap counts active tokens

- **WHEN** sudo magic-link sends for one challenge reach the configured re-send cap
- **THEN** a further send is rejected without issuing a new token, until an
  existing token is used or expires

#### Scenario: A reset challenge does not honor an old token

- **WHEN** a sudo challenge is reset (its prior instance had expired or been
  consumed) and a new send issues a fresh token
- **THEN** only a token from the new instance verifies; a token from the prior
  instance does not
