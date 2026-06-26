## ADDED Requirements

### Requirement: gitops validate command

The CLI SHALL provide `freeboard gitops validate <path>` that loads the YAML
config from `<path>` (a directory) using the `Freeboard.Core` loader and
validator, prints any errors, and exits `0` when validation passes or `1` when
validation fails or the input is invalid (including a missing or nonexistent
path). Errors SHALL be written to stderr and the success summary to stdout. The
command MUST NOT modify any file or external state and MUST NOT make any network
call.

#### Scenario: Valid config passes

- **WHEN** the user runs `freeboard gitops validate <dir>` on a valid config
- **THEN** the command prints a success summary (counts of standards, controls,
  scopes) to stdout and exits with code `0`

#### Scenario: Invalid config fails

- **WHEN** the user runs `freeboard gitops validate <dir>` on a config with
  validation errors
- **THEN** the command prints each error on its own line to stderr and exits with
  code `1`

#### Scenario: Missing path

- **WHEN** the user runs `freeboard gitops validate <dir>` and `<dir>` does not
  exist
- **THEN** the command prints an error naming the path to stderr and exits `1`

#### Scenario: Validate makes no network call

- **WHEN** the gitops load/validate code path is inspected by a structural test
- **THEN** it references no HTTP or socket APIs (no `System.Net.Http` or
  `System.Net.Sockets` usage), so validate cannot make an outbound network
  connection

### Requirement: gitops apply dry-run command

The CLI SHALL provide `freeboard gitops apply <path> --dry-run` that performs the
same load and validation as `validate` and additionally prints the resulting
desired config state that would be applied to stdout. In this increment
`--dry-run` is required; invoking `apply` without `--dry-run` SHALL exit `2` with
a message to stderr that only dry-run is supported and that real apply lands in a
later increment, because there is no backing store yet. `apply --dry-run` exits
`0` on a valid config and `1` on a validation or input error, and SHALL make no
network call.

#### Scenario: Dry-run prints planned state

- **WHEN** the user runs `freeboard gitops apply <dir> --dry-run` on a valid
  config
- **THEN** the command prints the standards, controls, and scopes that would be
  applied to stdout and exits `0` without writing any state

#### Scenario: Apply without dry-run is rejected

- **WHEN** the user runs `freeboard gitops apply <dir>` without `--dry-run`
- **THEN** the command prints to stderr that only `--dry-run` is supported in
  this version and that real apply lands in a later increment, and exits `2`

#### Scenario: Dry-run on invalid config fails

- **WHEN** the user runs `freeboard gitops apply <dir> --dry-run` on an invalid
  config
- **THEN** the command prints the validation errors to stderr and exits `1`
  without printing planned state

### Requirement: CLI stays community and cross-platform

The `gitops` command group SHALL live in `Freeboard.CLI`, reference only
`Freeboard.Core`, and contain no reference to `Freeboard.Enterprise`. It SHALL
run on Windows, Linux, and macOS without platform-specific code.

#### Scenario: No enterprise reference

- **WHEN** the solution is built
- **THEN** `Freeboard.CLI` resolves without any dependency on
  `Freeboard.Enterprise`

### Requirement: EE one-way rule pinned by architecture test

An architecture test SHALL assert that `Freeboard.Core`, `Freeboard.CLI`, and
`Freeboard.Agent` carry no project or assembly reference to
`Freeboard.Enterprise`, so an accidental reference fails the build.

#### Scenario: Community components free of enterprise

- **WHEN** the architecture test runs
- **THEN** it passes only if none of `Freeboard.Core`, `Freeboard.CLI`, or
  `Freeboard.Agent` references `Freeboard.Enterprise`
