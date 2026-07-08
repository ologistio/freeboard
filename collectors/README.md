# Freeboard collectors

A collector is a self-contained container that gathers evidence for one
evidence-collector and POSTs it to Freeboard's ingest endpoint. The container
carries no .NET runtime: transport is `curl` and the only local validation is a
`jq` syntactic-JSON check plus a `collector_id` assertion. Freeboard is the
authoritative validator (it returns `422` on any semantic violation); the wrapper
just catches a broken script early and prevents posting for the wrong collector.

This directory holds the reusable wrapper (`Dockerfile`, `entrypoint.sh`) and one
worked mock example (`example/collect.sh`). The full request contract is
`docs/evidence-ingest.md` and the JSON Schema is
`docs/schemas/evidence-ingest.v1.schema.json`.

## Environment contract

| Variable | Required | Meaning |
| --- | --- | --- |
| `FREEBOARD_BASE_URL` | yes | Base URL of the Freeboard web app, e.g. `https://freeboard.example`. The ingest URL is derived as `<base>/api/v1/freeboard/evidence`. |
| `FREEBOARD_COLLECTOR_ID` | yes | The evidence-collector id this container reports for. The wrapper asserts the payload's `collector_id` equals this. |
| `FREEBOARD_INGEST_TOKEN` | yes | The per-collector machine credential (raw bearer token) issued by an admin. Never baked into the image. |
| `FREEBOARD_RUN_ID` | no | Overrides the payload `run_id` (the idempotency key). Set it to make a retry dedupe against a specific run. |
| `FREEBOARD_COLLECTOR_SCRIPT` | no | Path to the collector script (default `/collector/collect.sh`). |
| `FREEBOARD_MAX_ATTEMPTS` | no | Ingest attempts before giving up (default 3). Only transient failures (transport errors, 5xx) are retried, with the identical body. |
| `FREEBOARD_RETRY_SLEEP` | no | Seconds between retries (default 2). |

The wrapper exits `0` only on `200`/`201`. Deterministic rejections
(`400/401/403/409/413/422`) exit non-zero without retrying; transient failures
retry with the identical body up to `FREEBOARD_MAX_ATTEMPTS`.

## Build and run the reference example

```sh
docker build -t freeboard-collector-example collectors/

# Issue a credential (admin), then run the collector against a live instance:
docker run --rm \
  -e FREEBOARD_BASE_URL="https://freeboard.example" \
  -e FREEBOARD_COLLECTOR_ID="google-workspace-mfa" \
  -e FREEBOARD_INGEST_TOKEN="v1.<secret>" \
  freeboard-collector-example
```

## Ologist-side dependency

The real, vendor-specific CE+ collector scripts (google-workspace, fleet, github,
vercel, endpoint-audit, ...) and their production image build inputs live
Ologist-side, NOT in this repo. They depend on the frozen contract in
`docs/evidence-ingest.md` and the JSON Schema. This wrapper base and the mock
example prove the shape those scripts must emit; a real script replaces
`example/collect.sh` (or sets `FREEBOARD_COLLECTOR_SCRIPT`) and reuses the
unchanged `entrypoint.sh`.
