#!/usr/bin/env node
// Reference collector wrapper for FaaS runtimes. Loads a collector module that returns a contract
// payload object, asserts it reports for this function's collector, then POSTs it to the Evidence
// ingest endpoint. Retries transient failures with the IDENTICAL body under the same run_id so a
// retry dedupes. Freeboard is the authoritative validator and returns 422 on any semantic violation,
// so the only local check is the collector_id assertion: never post for the wrong collector.
//
// Zero dependencies, so the same file deploys to every runtime. See README.md.

import { createServer } from "node:http";
import { pathToFileURL } from "node:url";
import { resolve } from "node:path";

const REQUIRED_ENV = ["FREEBOARD_BASE_URL", "FREEBOARD_COLLECTOR_ID", "FREEBOARD_INGEST_TOKEN"];

// Deterministic rejections: retrying the same body cannot help.
const FINAL_STATUSES = new Set([400, 401, 403, 409, 413, 422]);

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

// Number("bad") is NaN and Number("Infinity") is infinite; both make `attempt >= maxAttempts` never
// true, so a transport error or 5xx would retry forever - in HTTP-server mode until the process is
// killed. Reject anything that is not a finite positive integer rather than silently ignoring the
// documented budget.
function positiveInt(name, value, fallback) {
  if (value === undefined || value === "") {
    return fallback;
  }
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < 1) {
    throw new Error(`${name} must be a positive integer, got '${value}'`);
  }
  return parsed;
}

async function loadCollector() {
  const configured = process.env.FREEBOARD_COLLECTOR_MODULE;
  const specifier = configured
    ? pathToFileURL(resolve(configured)).href
    : new URL("./example/collect.mjs", import.meta.url).href;

  const module = await import(specifier);
  if (typeof module.default !== "function") {
    throw new Error(`collector module ${specifier} has no default-exported function`);
  }
  return module.default;
}

// Runs the collector and ingests its evidence. Resolves with the ingest response on 200/201;
// rejects on any failure so the platform records the invocation as failed.
export async function runOnce() {
  const missing = REQUIRED_ENV.filter((name) => !process.env[name]);
  if (missing.length > 0) {
    throw new Error(`missing required environment: ${missing.join(", ")}`);
  }

  const collectorId = process.env.FREEBOARD_COLLECTOR_ID;
  const collect = await loadCollector();
  const payload = await collect();

  if (payload?.collector_id !== collectorId) {
    throw new Error(
      `payload collector_id '${payload?.collector_id}' does not equal FREEBOARD_COLLECTOR_ID '${collectorId}'`,
    );
  }

  // When FREEBOARD_RUN_ID is set, stamp it so the caller controls the idempotency key.
  if (process.env.FREEBOARD_RUN_ID) {
    payload.run_id = process.env.FREEBOARD_RUN_ID;
  }

  const body = JSON.stringify(payload);
  const url = `${process.env.FREEBOARD_BASE_URL.replace(/\/+$/, "")}/api/v1/freeboard/evidence`;
  const maxAttempts = positiveInt("FREEBOARD_MAX_ATTEMPTS", process.env.FREEBOARD_MAX_ATTEMPTS, 3);
  const retrySeconds = positiveInt("FREEBOARD_RETRY_SLEEP", process.env.FREEBOARD_RETRY_SLEEP, 2);
  // Bound each POST: a hung connection would otherwise burn the whole invocation budget.
  const timeoutMs =
    positiveInt("FREEBOARD_TIMEOUT_SECONDS", process.env.FREEBOARD_TIMEOUT_SECONDS, 30) * 1000;

  for (let attempt = 1; ; attempt++) {
    let status = 0;
    let responseBody = "";

    try {
      const response = await fetch(url, {
        method: "POST",
        headers: {
          Authorization: `Bearer ${process.env.FREEBOARD_INGEST_TOKEN}`,
          "Content-Type": "application/json",
        },
        body,
        signal: AbortSignal.timeout(timeoutMs),
      });
      status = response.status;
      responseBody = await response.text();
    } catch (error) {
      responseBody = String(error);
    }

    if (status === 200 || status === 201) {
      return { status, body: responseBody };
    }
    if (FINAL_STATUSES.has(status)) {
      throw new Error(`ingest rejected (${status}): ${responseBody}`);
    }

    // Transient: transport error (status 0) or 5xx. Retry with the identical body (same run_id).
    if (attempt >= maxAttempts) {
      throw new Error(
        `ingest failed after ${attempt} attempt(s) (last status ${status}): ${responseBody}`,
      );
    }
    await sleep(retrySeconds * 1000);
  }
}

// AWS Lambda entry point (handler `index.handler`). Also the shape Azure's native Node model and
// the GCP functions-framework wrap; see README.md.
export async function handler() {
  const { status, body } = await runOnce();
  return { statusCode: status, body };
}

// HTTP-invoked runtimes (Cloud Run, Knative, OpenFaaS, Azure custom handler) start this file as the
// process and invoke it over the port they assign. Started directly with no port assigned it is a
// one-shot run, which is also the Kubernetes CronJob and Cloud Run Job mode.
const startedDirectly =
  process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url;
const port = process.env.PORT ?? process.env.FUNCTIONS_CUSTOMHANDLER_PORT;

if (startedDirectly) {
  if (port) {
    createServer(async (_request, response) => {
      try {
        const result = await runOnce();
        response.writeHead(200, { "Content-Type": "application/json" }).end(result.body);
      } catch (error) {
        response
          .writeHead(500, { "Content-Type": "application/json" })
          .end(JSON.stringify({ error: error.message }));
      }
    }).listen(Number(port));
  } else {
    try {
      const result = await runOnce();
      console.log(`evidence accepted (${result.status})`);
      console.log(result.body);
    } catch (error) {
      console.error(error.message);
      process.exit(1);
    }
  }
}
