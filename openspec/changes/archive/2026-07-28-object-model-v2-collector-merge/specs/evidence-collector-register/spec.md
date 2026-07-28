## REMOVED Requirements

### Requirement: Web evidence-collector register page

**Reason**: The `EvidenceCollector` kind is merged into the unified `Collector` kind, so
its dedicated register page is replaced by the single collector register at
`/settings/collectors`. This capability is superseded in full by `collector-register`.
**Migration**: Open `/settings/collectors`. It renders every control with its `evaluation`
rule and every attached collector, adding the collector's `provider` and, for a `manual`
or `training` collector, its form. Every property of the retired page (authenticated-only,
GET-only, served in read-only mode, in-page notice on an unreachable store, no
accessible-organisation narrowing, reachable from the shell navigation) is carried
forward by the `collector-register` capability's "Web collector register page"
requirement. The `/settings/evidence-collectors` URL is retired with no redirect.

### Requirement: CLI evidence-collector register command

**Reason**: Replaced by the `collector-register` capability's `freeboard collector list`
command, which reads the merged register.
**Migration**: Run `freeboard collector list`. The verb is unchanged; it now also prints a
collector's `provider` and an attestation collector's form, and reads
`GET /api/v1/freeboard/collectors` instead of the retired
`GET /api/v1/freeboard/evidence-collectors`. The exit-code convention is unchanged.
