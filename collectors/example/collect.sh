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
  "run_id": "${FREEBOARD_RUN_ID:-${now}-example}",
  "collector_version": "reference-example",
  "started_at": "${now}",
  "finished_at": "${now}",
  "checks": [
    {
      "name": "example-check",
      "severity": "hard",
      "status": "pass",
      "detail": "Reference mock check; always passes."
    }
  ],
  "metadata": {
    "source": "reference-example"
  }
}
JSON
