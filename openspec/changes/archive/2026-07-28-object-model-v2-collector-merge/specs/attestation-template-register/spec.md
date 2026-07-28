## REMOVED Requirements

### Requirement: Web attestation-template register page

**Reason**: The `AttestationTemplate` kind is merged into the unified `Collector` kind, so
its dedicated register page is replaced by the single collector register at
`/settings/collectors`. This capability is superseded in full by `collector-register`.
**Migration**: Open `/settings/collectors`. It renders every control's collectors,
including a `manual` or `training` collector's `body`, `fields`, `pass_mark`, and `quiz`
read from its `config`. Every property of the retired page (the HTML-encoded body, the
redacted quiz answer, authenticated-only, GET-only, served in read-only mode, in-page
notice on an unreachable store, no accessible-organisation narrowing, reachable from the
shell navigation) is carried forward by the `collector-register` capability's "Web
collector register page" requirement. The `/settings/attestation-templates` URL is retired
with no redirect.

### Requirement: CLI attestation-template register command

**Reason**: Replaced by the `collector-register` capability's `freeboard collector list`
command, which prints an attestation collector's form alongside every other collector. The
`attestation-template` command group is removed.
**Migration**: Run `freeboard collector list`. It prints each control with its collectors,
including a `manual` collector's fields and a `training` collector's pass mark and quiz
items, and still prints no quiz `answer`. The exit-code convention is unchanged.
