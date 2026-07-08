#!/bin/sh
# Worked reference/mock collector. Emits a fixed, contract-valid evidence-ingest payload on stdout so
# the wrapper and the ingest endpoint can be exercised end-to-end without a real vendor integration.
# A real collector replaces this script; the entrypoint and image are unchanged.
set -eu

now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat <<JSON
{
  "schema_version": "freeboard.evidence.v1",
  "collector_id": "${FREEBOARD_COLLECTOR_ID:-example-collector}",
  "organisation_id": "${FREEBOARD_ORGANISATION_ID:-example-org}",
  "requirement_id": "${FREEBOARD_REQUIREMENT_ID:-example-requirement}",
  "run_id": "${FREEBOARD_RUN_ID:-${now}-example}",
  "collector_version": "reference-example",
  "collected_at": "${now}",
  "checks": [
    {
      "name": "example-check",
      "severity": "hard",
      "result": "pass",
      "detail": "Reference mock check; always passes."
    }
  ],
  "metadata": {
    "source": "reference-example"
  }
}
JSON
