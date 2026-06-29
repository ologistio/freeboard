# user-cli Specification

## Purpose
TBD - created by archiving change add-authentication. Update Purpose after archive.
## Requirements
### Requirement: user command group administers via the HTTP API

The CLI SHALL provide a `user` command group that administers users through the
authenticated Freeboard HTTP API, NOT by touching the database directly. The `user`
commands SHALL NOT use the persistence user store, SHALL NOT hash passwords, and SHALL
NOT require any database connection string or shared pepper. The `user` group SHALL
live in `Freeboard.CLI`, contain no reference to `Freeboard.Enterprise`, and remain
cross-platform (Windows, Linux, macOS). Only `system migrate` retains direct database
access in the CLI.

#### Scenario: user commands call the API, not the DB

- **WHEN** a `user` administration subcommand runs
- **THEN** it issues an authenticated HTTP request to the Freeboard API and does not
  open a database connection

#### Scenario: user group has no enterprise reference

- **WHEN** the solution is built
- **THEN** `Freeboard.CLI` resolves the `user` command group without any dependency on
  `Freeboard.Enterprise`

### Requirement: API client configuration and exit codes

Every `user` subcommand that calls the API SHALL accept an API base URL (a `--api-url`
option or `FREEBOARD_API_URL` env var) and an admin bearer token (a `--token` option
or `FREEBOARD_ADMIN_TOKEN` env var), the option overriding the env var. If the base
URL or token is missing where required, or the API rejects the token, the command
SHALL print a clear message and exit `3`. Exit codes follow the existing convention:
`0` success, `1` input/validation error, `3` operational failure (including HTTP
`401`/`403`/`5xx` and connection failures).

#### Scenario: Missing token exits 3

- **WHEN** a `user` subcommand that needs admin auth runs with neither `--token` nor
  `FREEBOARD_ADMIN_TOKEN` set
- **THEN** the command prints a clear message and exits `3`

#### Scenario: API rejects the token

- **WHEN** the API returns `401` or `403` for a `user` subcommand
- **THEN** the command prints a clear message and exits `3`

### Requirement: user administration subcommands

The CLI SHALL provide `user create`, `user list`, `user reset-password`,
`user enable`, and `user disable`, each mapping to an admin API call. `create` SHALL
create a user with an email, name, and role via the API (the server hashes the
password) and SHALL print the one-time temporary password returned by the API exactly
once. `reset-password` SHALL trigger the server-side password reset and session
revocation and SHALL print the returned one-time temporary password exactly once.
`disable`/`enable` SHALL set the account's enabled state via the API. On a duplicate
email or invalid input the command SHALL surface the API's validation error and exit
`1`; on operational/API failure it SHALL exit `3`; on success it SHALL exit `0`.

#### Scenario: Create prints the one-time temporary password

- **WHEN** `user create` runs with valid admin auth and a new email
- **THEN** it calls the API to create the user, prints the returned temporary password
  once, and exits `0`

#### Scenario: Duplicate email exits 1

- **WHEN** `user create` runs with an email the API reports as already existing
- **THEN** the command prints the validation error and exits `1`

#### Scenario: Disable prevents login

- **WHEN** an administrator runs `user disable` for a user
- **THEN** the API marks the account disabled and that user can no longer authenticate

### Requirement: user bootstrap creates the first admin

The CLI SHALL provide `user bootstrap` that creates the first administrator by calling
the API bootstrap endpoint (`POST /api/v1/freeboard/setup`) with a one-time bootstrap
secret (a `--bootstrap-secret` option or env var) and the new admin's details. It
SHALL print the returned admin token on success. It SHALL NOT require an existing admin
token (it solves the no-users-yet chicken-and-egg). If the API reports that a user
already exists it SHALL exit `3` with a clear message; on a wrong/absent bootstrap
secret it SHALL exit `3`.

#### Scenario: Bootstrap creates the first admin and prints a token

- **WHEN** `user bootstrap` runs before any admin has been created with the correct
  bootstrap secret
- **THEN** the first admin is created via the API and the returned admin token is
  printed, and the command exits `0`

#### Scenario: Bootstrap after a user exists exits 3

- **WHEN** `user bootstrap` runs but the deployment already has a user
- **THEN** the API returns a conflict, the command prints a clear message, and exits
  `3`

