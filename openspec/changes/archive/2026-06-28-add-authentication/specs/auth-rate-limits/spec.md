## ADDED Requirements

### Requirement: Storage-agnostic rate-limit seam

Rate limiting SHALL sit behind an `IAuthRateLimitStore` abstraction whose contract is
storage-agnostic and SHALL NOT leak SQL or database semantics, so a Redis-backed
implementation can replace the default MySQL one for multi-server scale without
changing callers. The interface SHALL expose only storage-neutral operations: an
atomic check-and-increment that returns the current state (and any retry-after), a
reset for a given bucket key, and a prune for retention. The MySQL implementation is
the default; a Redis implementation is a drop-in behind the same seam.

#### Scenario: Default MySQL impl behind the seam

- **WHEN** the web app is configured with the default rate-limit store
- **THEN** it uses the MySQL-backed `IAuthRateLimitStore` and the contract exposes no
  SQL types to callers

#### Scenario: Redis impl is a drop-in

- **WHEN** a Redis-backed `IAuthRateLimitStore` is registered instead
- **THEN** the auth endpoints work unchanged because they depend only on the
  storage-agnostic interface

### Requirement: Separate per-account and per-IP rate limiting

The web app SHALL rate-limit authentication attempts on the login endpoint, the
MFA-verify endpoints (including magic-link send), AND the step-up endpoint
(`POST /api/v1/freeboard/auth/sudo`) using SEPARATE buckets, NOT a single composite
bucket: a per-account bucket keyed by the normalized email, and a per-IP bucket keyed
by the trusted client IP (an optional combined account+IP bucket MAY also be used).
The sudo and MFA-verify endpoints are rate-limited because each checks a factor or
password and would otherwise be an online guessing oracle. A request SHALL be limited
when ANY applicable bucket is over its configured threshold, returning HTTP `429`
with a `retry-after` header and SHALL NOT perform the expensive password hash or
factor verification. Thresholds and windows SHALL be configurable.

#### Scenario: Rotating IPs against one account is still limited

- **WHEN** repeated failed logins for one account arrive from many different IPs
- **THEN** the per-account bucket trips and further attempts receive `429`

#### Scenario: Rotating accounts from one IP is still limited

- **WHEN** repeated failed logins for many different accounts arrive from one IP
- **THEN** the per-IP bucket trips and further attempts receive `429`

### Requirement: Trusted client IP extraction

The client IP used for the per-IP bucket SHALL be the socket remote IP UNLESS the
request arrives through a configured trusted proxy, in which case the forwarded
client IP SHALL be taken per ASP.NET Core forwarded-headers handling restricted to
configured known proxies/networks. `X-Forwarded-For` SHALL NOT be trusted blindly, so
a client cannot spoof its IP to evade the per-IP bucket.

#### Scenario: Spoofed X-Forwarded-For is ignored

- **WHEN** a request that did not arrive through a trusted proxy sends an
  `X-Forwarded-For` header
- **THEN** the per-IP bucket uses the socket remote IP, not the spoofed header value

### Requirement: Rate limit is not an enumeration oracle

The per-account bucket SHALL be locked and `429` SHALL be returned even for emails
that do not correspond to an existing account (the bucket is keyed by the normalized
email string regardless of account existence). The `429` status, body, and
`retry-after` SHALL be identical whether or not the account exists, so a `429` does
not reveal account existence.

#### Scenario: 429 identical for known and unknown accounts

- **WHEN** the per-account threshold is exceeded for a real email and for a
  nonexistent email
- **THEN** both receive an identical `429` response that does not distinguish them

### Requirement: Reset on success applies only to the account bucket

A successful authentication SHALL reset ONLY the per-account bucket. The per-IP
bucket SHALL persist so that one successful login from a shared or NAT IP does not
clear throttling earned by other failing accounts behind that IP.

#### Scenario: Successful login does not clear the IP bucket

- **WHEN** one account behind a shared IP logs in successfully while other accounts
  from that IP are failing
- **THEN** the per-account bucket for the successful account resets but the per-IP
  bucket remains in force

### Requirement: Atomic bucket schema and increment

The `auth_rate_limits` table SHALL have a composite UNIQUE/PRIMARY KEY on
`(bucket_kind, bucket_key)` where `bucket_kind` distinguishes account, IP, and
account+IP buckets and `bucket_key` is the normalized email or IP string. Increment
SHALL be an atomic upsert (`INSERT ... ON DUPLICATE KEY UPDATE`) that rolls the window
and computes the lockout in the same statement, so concurrent attempts cannot lose
increments. The limit SHALL be enforced server-side and SHALL survive a process
restart because it is persisted.

#### Scenario: Concurrent attempts do not lose increments

- **WHEN** multiple attempts for the same bucket arrive concurrently
- **THEN** the atomic upsert counts each one and the threshold is enforced correctly

#### Scenario: Limit persists across a restart

- **WHEN** the web app restarts between failed attempts
- **THEN** the persisted attempt count is still in force because it is stored in the
  database, not only in process memory

### Requirement: Rate-limit rows are pruned

The `auth_rate_limits` table SHALL be pruned to bound its growth, because the
per-account bucket is keyed by the submitted email even when no account exists and an
attacker can therefore create attacker-controlled rows. The store SHALL provide a
prune operation that deletes rows that are NO LONGER enforcing a lockout
(`locked_until IS NULL` or `locked_until <= now`) AND whose window is older than a
configured retention (`window_started_at <= now - retention`). The retention SHALL be
at least the longest lockout window so an in-force lockout is never pruned early.
Pruning SHALL run opportunistically (a bounded delete from the limiter path) and/or
via a CLI sweep; it SHALL NOT delete a row that is currently enforcing a lockout.

#### Scenario: Stale unlocked rows are pruned

- **WHEN** prune runs and a row has no active lockout and a window older than the
  retention
- **THEN** the row is deleted

#### Scenario: An active lockout is not pruned

- **WHEN** prune runs and a row's `locked_until` is in the future
- **THEN** the row is retained and the lockout still applies
