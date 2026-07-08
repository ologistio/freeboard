# collector-credentials Specification

## Purpose
TBD - created by archiving change add-evidence-ingest-endpoint. Update Purpose after archive.
## Requirements
### Requirement: Per-collector machine credential stored as a keyed hash

The system SHALL support a per-collector machine credential: an opaque high-entropy bearer
token scoped to exactly one `EvidenceCollector` id, not tied to any human user. The token
SHALL reuse the existing prefixed keyed-HMAC token primitive (`ITokenHasher.MintPrefixed`
/ `TryHashPrefixed`, wire format `v<keyId>.<secret>`), and the system SHALL store only the
keyed HMAC-SHA256 of the secret together with its `token_key_version`, never the raw token,
in a dedicated `collector_credentials` table keyed to the collector id. The raw token SHALL
be returned exactly once, at issuance. A credential row SHALL record its collector id, the
token hash, the key version, a created-at, an optional last-seen-at, an optional expiry, and
a revoked state. A credential SHALL support an OPTIONAL nullable `expires_at`: when set, an
elapsed expiry SHALL make the credential no longer usable for ingest - the credential is still
recognised and authenticates successfully, but it does NOT authorize ingest and is rejected
with `403` by the ingest policy (it does not silently `401` as if unknown); when null, the
credential does not expire (collectors run unattended) and revocation is the control for
retiring it.

#### Scenario: Raw collector token is returned once and never stored

- **WHEN** an administrator issues a collector credential
- **THEN** the raw token is returned exactly once in the issue response and the store holds
  only its keyed HMAC plus the key version, never the raw token

#### Scenario: Credential is scoped to one collector

- **WHEN** a collector credential is issued for a collector id
- **THEN** the stored row binds the credential to that single collector id, and the token
  authorises ingest only for that collector

#### Scenario: Expired credential authenticates but is not authorized for ingest

- **WHEN** a credential carries a non-null `expires_at` that has elapsed and its token is
  presented to the ingest endpoint
- **THEN** the credential is still recognised and authenticates successfully, but it lacks the
  usable claim, so the ingest policy rejects the request with `403` (not a `401` as if the
  credential were unknown)

### Requirement: Route-scoped collector authentication scheme

The web app SHALL validate a collector credential through a route-scoped authentication
scheme applied only to the Evidence ingest endpoint, leaving the human session scheme
unchanged. The scheme SHALL parse the token key id from the `v<keyId>.` prefix, compute the
keyed HMAC with the selected key, look the credential up by token hash in
`collector_credentials`, and assert the stored key version matches. It SHALL reject a missing,
malformed, unknown-key, or unknown (hash-not-found) credential with a uniform `401` and no
oracle, by failing authentication (a failed authentication yields `401`, never `403`).

A recognised credential (its token hash matches a stored row and the key version matches) SHALL
authenticate successfully, resolving to a non-human collector principal carrying the collector
id and a claim recording whether the credential is currently usable: usable exactly when it is
not revoked and not expired. A recognised credential that is revoked or expired SHALL still
authenticate but SHALL NOT carry the usable claim, and SHALL be rejected with `403` by the
ingest route's authorization requirement (which requires the collector-id and usable claims).
This mechanism is required because an authentication failure can only yield `401`; `403` for a
once-valid, now-forbidden credential is reachable only when authentication succeeds and a later
authorization requirement denies. A valid, usable credential SHALL be admitted.

The scheme SHALL NOT accept a human session token, and the human session scheme SHALL NOT
accept a collector token, because each scheme looks up only its own credential table (disjoint
lookup tables).

#### Scenario: Valid collector token authenticates as its collector

- **WHEN** a request to the ingest endpoint presents a valid, unrevoked, unexpired collector
  token
- **THEN** the request is authenticated as a collector principal carrying that credential's
  collector id

#### Scenario: Malformed or unknown token rejected with a uniform 401

- **WHEN** a collector token has a missing or malformed key-id prefix, an unknown or retired
  key id, a non-base64url secret, or a hash that matches no stored credential
- **THEN** the response is a uniform `401` with no distinguishing oracle

#### Scenario: Revoked or expired credential is rejected with 403

- **WHEN** a recognised credential (its token hash matches a stored row) has been revoked or
  has an elapsed `expires_at`
- **THEN** the response is `403`, distinguishing a once-valid but now-forbidden credential
  from an unrecognised one

#### Scenario: Collector token rejected on human endpoints

- **WHEN** a collector token is presented to a human-session-protected endpoint
- **THEN** the response is `401`, because that endpoint's scheme looks up only the sessions
  table

#### Scenario: Human session token rejected at ingest

- **WHEN** a valid human session token is presented to the ingest endpoint
- **THEN** the response is `401`, because the ingest scheme looks up only the
  `collector_credentials` table

### Requirement: Admin issuance and revocation of collector credentials

The web app SHALL provide endpoints under `/api/v1/freeboard/` to issue a credential for a
named collector (with an optional expiry) and to revoke a credential, and the CLI SHALL provide
matching `freeboard collector credential issue` and `freeboard collector credential revoke`
subcommands that call those endpoints over HTTP (not by direct database access), reusing the
existing admin-token CLI pattern and its exit-code convention (`0` success, `1` validation, `3`
operational failure). Credential issuance and revocation are admin config actions and SHALL NOT
be exempt from GitOps read-only mode. Issuing a credential for a collector that is not in the
register SHALL be rejected with a validation response and SHALL NOT create a credential.
Revoking a credential SHALL make its token no longer authorize ingest: a revoked token is
still recognised and authenticates successfully, but it does NOT carry the usable claim and the
ingest policy rejects it with `403` (it does not silently `401` as if unknown).

Both endpoints SHALL be gated by the system-administration permission (`system.admin`),
force-enforced, using the same route-permission mechanism the codebase applies to other
system-admin-only routes - NOT a legacy global-role check - so a non-admin caller is denied
with `403`. The route-metadata guard SHALL assert both endpoints carry this force-enforced
permission, so an ungated or mis-wired credential route fails the build rather than shipping
open.

#### Scenario: Admin issues a credential for a known collector

- **WHEN** an administrator issues a credential for a collector present in the register
- **THEN** a credential is persisted, the raw token is returned once, and the token then
  authenticates ingest for that collector

#### Scenario: Issuing for an unknown collector is rejected

- **WHEN** an administrator issues a credential for a collector id not in the register
- **THEN** the request is rejected with a validation response and no credential is created

#### Scenario: Revoked credential is rejected at ingest with 403

- **WHEN** an administrator revokes a collector credential and the collector then reuses the
  same token
- **THEN** the token is still recognised and authenticates, but it lacks the usable claim, so
  the ingest request is rejected with `403` (not a `401`) and no Evidence is persisted

#### Scenario: Non-admin cannot issue or revoke

- **WHEN** a non-admin caller invokes the issue or revoke endpoint
- **THEN** the request is denied with `403` by the force-enforced `system.admin` permission gate
  and no credential is created or revoked

