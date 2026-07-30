#!/bin/sh
# Reference collector entrypoint. Runs a collector script that emits contract JSON on stdout, does the
# only validation a shell can (syntactic JSON via `jq empty` and a collector_id assertion - the server
# is the authoritative validator and returns 422 on any semantic violation), then POSTs the result to
# the Evidence ingest endpoint. Retries transient failures with the IDENTICAL body under the same
# run_id so a retry dedupes. Exits non-zero on any ingest failure.
set -eu

: "${FREEBOARD_BASE_URL:?FREEBOARD_BASE_URL is required (e.g. https://freeboard.example)}"
: "${FREEBOARD_COLLECTOR_ID:?FREEBOARD_COLLECTOR_ID is required}"
: "${FREEBOARD_INGEST_TOKEN:?FREEBOARD_INGEST_TOKEN is required}"

COLLECTOR_SCRIPT="${FREEBOARD_COLLECTOR_SCRIPT:-/collector/collect.sh}"

payload="$("$COLLECTOR_SCRIPT")" || { echo "collector script failed" >&2; exit 1; }

# Syntactic JSON only. A curl/sh wrapper cannot validate a JSON Schema, so it does not claim to.
if ! printf '%s' "$payload" | jq empty >/dev/null 2>&1; then
    echo "collector output is not valid JSON" >&2
    exit 1
fi

# Never post for the wrong collector: the payload's collector_id must be present and match this image's.
payload_id="$(printf '%s' "$payload" | jq -r '.collector_id // empty')"
if [ -z "$payload_id" ] || [ "$payload_id" != "$FREEBOARD_COLLECTOR_ID" ]; then
    echo "payload collector_id '$payload_id' does not equal FREEBOARD_COLLECTOR_ID '$FREEBOARD_COLLECTOR_ID'" >&2
    exit 1
fi

# When FREEBOARD_RUN_ID is set, stamp it so the caller controls the idempotency key.
if [ -n "${FREEBOARD_RUN_ID:-}" ]; then
    payload="$(printf '%s' "$payload" | jq --arg r "$FREEBOARD_RUN_ID" '.run_id = $r')"
fi

url="${FREEBOARD_BASE_URL%/}/api/v1/freeboard/evidence"

attempt=1
max_attempts="${FREEBOARD_MAX_ATTEMPTS:-3}"
while :; do
    http_code="$(printf '%s' "$payload" | curl -sS -o /tmp/ingest_response -w '%{http_code}' \
        -X POST "$url" \
        -H "Authorization: Bearer ${FREEBOARD_INGEST_TOKEN}" \
        -H "Content-Type: application/json" \
        --data-binary @-)" || http_code="000"

    case "$http_code" in
        200 | 201)
            echo "evidence accepted (${http_code})"
            cat /tmp/ingest_response
            exit 0
            ;;
        400 | 401 | 403 | 409 | 413 | 422)
            # Deterministic rejections: retrying the same body cannot help.
            echo "ingest rejected (${http_code})" >&2
            cat /tmp/ingest_response >&2 || true
            exit 1
            ;;
    esac

    # Transient: transport error (000) or 5xx. Retry with the identical body (same run_id -> dedupe).
    if [ "$attempt" -ge "$max_attempts" ]; then
        echo "ingest failed after ${attempt} attempt(s) (last status ${http_code})" >&2
        exit 1
    fi
    attempt=$((attempt + 1))
    sleep "${FREEBOARD_RETRY_SLEEP:-2}"
done
