## ADDED Requirements

### Requirement: Admin user-management endpoints

The web app SHALL provide admin user-management endpoints under `/api/v1/freeboard/`,
all behind an administrator authorization policy: a request from a non-admin
authenticated user SHALL receive `403`, and an unauthenticated request SHALL receive
`401`. Ids in paths and bodies are ULID strings. Validation failures SHALL return
`422` with a validation-error body. The endpoints are the contract the CLI `user`
commands consume; they are the only way the CLI manages users (the CLI does not touch
the database).

The endpoints SHALL be:

- `POST /api/v1/freeboard/users` - create a user from `{ email, name, global_role }`.
  The server SHALL generate a cryptographically random ONE-TIME temporary password,
  store its Argon2id hash, set the new user's `force_password_reset` to true, and
  return `{ user, temporary_password }` where `user` includes the ULID `id`. The
  `temporary_password` is returned exactly ONCE in this response and is never stored in
  plaintext or retrievable later. No email is sent (email is used only by
  password/forgot and magic-link). A duplicate normalized email or invalid input SHALL
  return `422`.
- `GET /api/v1/freeboard/users` - list users (admin only); list responses SHALL NOT
  include any password.
- `GET /api/v1/freeboard/users/{id}` - return one user, or `404` if absent.
- `POST /api/v1/freeboard/users/{id}/disable` - set the account disabled. Disabling
  SHALL revoke that user's sessions.
- `POST /api/v1/freeboard/users/{id}/enable` - clear the disabled state.
- `POST /api/v1/freeboard/users/{id}/reset-password` - generate a new ONE-TIME
  temporary password, store its Argon2id hash, set the user's `force_password_reset`
  to true, revoke that user's sessions, and return `{ temporary_password }` exactly
  once. No email is sent.

The user then logs in with the temporary password (which forces them into the limited
session, per the session capability) and sets a new password via
`POST /api/v1/freeboard/account/password`.

#### Scenario: Create returns a one-time temporary password and forces a reset

- **WHEN** an admin calls `POST /api/v1/freeboard/users` with a new email, name, and
  role
- **THEN** a user is created with a ULID id and `force_password_reset` true, the
  response includes the `temporary_password` exactly once, and the password is stored
  only as an Argon2id hash

#### Scenario: Reset-password returns a one-time temporary password

- **WHEN** an admin calls `POST /api/v1/freeboard/users/{id}/reset-password`
- **THEN** the response includes a fresh `temporary_password` once, the user's
  `force_password_reset` is set, and that user's sessions are revoked

#### Scenario: Non-admin is forbidden

- **WHEN** a non-admin authenticated user calls any admin user-management endpoint
- **THEN** the response is `403` and no change is made

#### Scenario: Duplicate email returns 422

- **WHEN** an admin creates a user whose normalized email already exists
- **THEN** the response is `422` and no user is created

#### Scenario: Unknown user returns 404

- **WHEN** an admin requests `GET /api/v1/freeboard/users/{id}` for an id that does
  not exist
- **THEN** the response is `404`

#### Scenario: Disable revokes the user's sessions

- **WHEN** an admin calls `POST /api/v1/freeboard/users/{id}/disable` for an enabled
  user
- **THEN** the account is disabled and that user's existing sessions can no longer
  authenticate
