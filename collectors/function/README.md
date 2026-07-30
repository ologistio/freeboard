# Freeboard collectors: FaaS

The function runtime for a collector. Use it where a collector reaches a vendor
API over the network on a schedule and needs no host of its own: the platform
owns the schedule, the retry policy, and the run history, and there is nothing to
patch between runs.

`index.mjs` is the reusable wrapper and `example/collect.mjs` is one worked mock
collector. It is plain ESM with no dependencies and no .NET runtime, so the same
file deploys unchanged to every platform below. The only local validation is the
`collector_id` assertion, which stops the function posting for the wrong
collector; Freeboard is the authoritative validator and returns `422` on any
semantic violation. The full request contract is `docs/evidence-ingest.md` and
the JSON Schema is `docs/schemas/evidence-ingest.v1.schema.json`.

Use `collectors/docker/` instead when the collector needs a toolchain in its
image, and `collectors/systemd/` when it must run on a specific host.

## The collector module

A collector is an ESM module with a default-exported function returning the
contract payload as an object:

```js
export default async function collect() {
  return { schema_version: "freeboard.evidence.v1", /* ... */ };
}
```

Point `FREEBOARD_COLLECTOR_MODULE` at it. A real collector replaces
`example/collect.mjs`; `index.mjs` is unchanged.

## Environment contract

| Variable | Required | Meaning |
| --- | --- | --- |
| `FREEBOARD_BASE_URL` | yes | Base URL of the Freeboard web app, e.g. `https://freeboard.example`. The ingest URL is derived as `<base>/api/v1/freeboard/evidence`. |
| `FREEBOARD_COLLECTOR_ID` | yes | The collector id this function reports for. The wrapper asserts the payload's `collector_id` equals this. |
| `FREEBOARD_INGEST_TOKEN` | yes | The per-collector machine credential (raw bearer token) issued by an admin. Inject it from the platform's secret store; never commit it to a deployment manifest. |
| `FREEBOARD_ORGANISATION_ID` | example | The organisation the run reports for. Required by the contract; the mock example reads it from here. A real collector emits it directly. |
| `FREEBOARD_REQUIREMENT_ID` | example | The requirement the run reports for. Required by the contract; the mock example reads it from here. A real collector emits it directly. |
| `FREEBOARD_COLLECTOR_MODULE` | no | Path to the collector module (default `./example/collect.mjs`). Resolved against the process working directory. |
| `FREEBOARD_RUN_ID` | no | Overrides the payload `run_id` (the idempotency key). Set it to make a retry dedupe against a specific run. |
| `FREEBOARD_MAX_ATTEMPTS` | no | Ingest attempts before giving up (default 3). Only transient failures (transport errors, 5xx) are retried, with the identical body. |
| `FREEBOARD_RETRY_SLEEP` | no | Seconds between retries (default 2). |
| `FREEBOARD_TIMEOUT_SECONDS` | no | Per-attempt POST timeout (default 30). Keep `MAX_ATTEMPTS * (TIMEOUT_SECONDS + RETRY_SLEEP)` below the platform's invocation timeout. |

A run succeeds only on `200`/`201`. Deterministic rejections
(`400/401/403/409/413/422`) fail without retrying; transient failures retry with
the identical body up to `FREEBOARD_MAX_ATTEMPTS`.

## Invocation modes

`index.mjs` offers three entry shapes; pick the one the platform expects.

| Mode | Entry | Used by |
| --- | --- | --- |
| Lambda handler | `index.handler` | AWS Lambda |
| HTTP server | `node index.mjs` with a port assigned (`PORT` or `FUNCTIONS_CUSTOMHANDLER_PORT`) | Cloud Run, Knative, OpenFaaS, Azure custom handler |
| One-shot | `node index.mjs` with no port assigned | Kubernetes CronJob, Cloud Run Job, local testing |

Run it locally against a live instance:

```sh
cd collectors/function
FREEBOARD_BASE_URL="https://freeboard.example" \
FREEBOARD_COLLECTOR_ID="google-workspace-mfa" \
FREEBOARD_ORGANISATION_ID="org-acme" \
FREEBOARD_REQUIREMENT_ID="req-mfa" \
FREEBOARD_INGEST_TOKEN="v1.<secret>" \
node index.mjs
```

## Platform requirements

Common to all four: Node 18 or later (`fetch` and `AbortSignal.timeout` are
built in), egress to the Freeboard base URL, the credential injected from a
secret store, and an invocation timeout above the retry budget. Nothing else is
needed - no build step, no bundler, no package manager.

Freeboard evaluates collector staleness from the runs it receives, so the
schedule must be at least as frequent as the collector's staleness window and
the platform must surface failed invocations. A failed invocation is retried at
the next tick under a new `run_id`; that is a new run, not a replay.

### AWS Lambda

- Runtime `nodejs22.x`, handler `index.handler`, architecture either.
- Package: zip `index.mjs` plus the collector module at the archive root. No
  layer and no build step.
- Schedule: an EventBridge Scheduler schedule targeting the function. The event
  payload is ignored.
- Credential: a Secrets Manager or SSM Parameter Store secret, mapped to
  `FREEBOARD_INGEST_TOKEN`. A plain Lambda environment variable stores the token
  in the function configuration, where anyone with `lambda:GetFunction` can read
  it.
- Timeout: default is 3 seconds, below the retry budget. Raise it.
- The handler rejects on failure, so Lambda records the invocation as an error;
  route the failure destination or alarm on `Errors`.

### Azure Functions

Two options. The custom handler keeps the zero-dependency property:

- `host.json` runs Node as the handler process. The host assigns
  `FUNCTIONS_CUSTOMHANDLER_PORT`, which `index.mjs` listens on:

  ```json
  {
    "version": "2.0",
    "customHandler": {
      "description": {
        "defaultExecutablePath": "node",
        "arguments": ["index.mjs"]
      }
    }
  }
  ```

- Add a timer-triggered function alongside it - a folder named for the function
  containing `function.json` with a `timerTrigger` binding and its NCRONTAB
  `schedule`. The host POSTs to `/<functionName>` on each tick.
- Credential: an app setting sourced from Key Vault
  (`@Microsoft.KeyVault(...)`), not a literal value.
- Timeout: `functionTimeout` in `host.json`; the Consumption plan caps it at 10
  minutes.

The native Node v4 model instead needs the `@azure/functions` dependency and a
few lines of registration:

```js
import { app } from "@azure/functions";
import { runOnce } from "./index.mjs";

app.timer("collector", { schedule: "0 0 * * * *", handler: () => runOnce() });
```

### Google Cloud

A Cloud Run Job is the closest fit and needs no dependency: it is one-shot, so
it uses the plain `node index.mjs` mode and exits.

- Container: any Node 18+ base image, `CMD ["node", "index.mjs"]`.
- Schedule: Cloud Scheduler calling the Jobs Run API through a service account.
- Credential: a Secret Manager secret exposed as an environment variable.
- Timeout: the job's task timeout; set task retries to 0 and let the next
  scheduled run be the retry, so a failure is visible rather than absorbed.

For a native Cloud Run function (`gcloud functions deploy --gen2`) the
functions-framework is required as a dependency and registers the entry point:

```js
import functions from "@google-cloud/functions-framework";
import { runOnce } from "./index.mjs";

functions.http("collector", async (_req, res) => res.json(await runOnce()));
```

Deploy with `--runtime nodejs22 --trigger-http --no-allow-unauthenticated` and
drive it from Cloud Scheduler with an OIDC token.

### Kubernetes FaaS

- Plain `CronJob`: the lowest-liability option and the one to reach for unless
  scale-to-zero HTTP is already in place. Image runs `node index.mjs`,
  `restartPolicy: Never`, `backoffLimit: 0`, credential from a `Secret` via
  `secretKeyRef` (not a `ConfigMap`), and `concurrencyPolicy: Forbid` so a slow
  run is not overlapped by the next tick.
- Knative Serving: deploy `index.mjs` as a Service; it listens on `$PORT`, which
  Knative sets. Drive it with a Knative Eventing `PingSource` on a schedule.
  Set `containerConcurrency: 1` and confirm the request timeout exceeds the
  retry budget - the default 300 seconds is normally enough. Scale-to-zero means
  a cold start per tick.
- OpenFaaS: use the `of-watchdog` HTTP mode with `mode: http` and
  `upstream_url: http://127.0.0.1:8080`, then set `PORT=8080` in the function's
  environment so `index.mjs` binds there. Schedule with the cron connector
  (`topic: cron-function`, `schedule` annotation). Store the credential as an
  OpenFaaS secret, not a build argument.
