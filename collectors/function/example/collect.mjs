// Worked reference/mock collector. Returns a fixed, contract-valid evidence-ingest payload so the
// wrapper and the ingest endpoint can be exercised end-to-end without a real vendor integration.
// A real collector replaces this module; index.mjs is unchanged.

export default async function collect() {
  const collectedAt = new Date().toISOString();
  // run_id must not contain ':' - that character delimits collector_id from run_id server-side.
  const runId = collectedAt.replace(/[:.]/g, "-");

  return {
    schema_version: "freeboard.evidence.v1",
    collector_id: process.env.FREEBOARD_COLLECTOR_ID ?? "example-collector",
    organisation_id: process.env.FREEBOARD_ORGANISATION_ID ?? "example-org",
    requirement_id: process.env.FREEBOARD_REQUIREMENT_ID ?? "example-requirement",
    run_id: process.env.FREEBOARD_RUN_ID ?? `${runId}-example`,
    collector_version: "reference-example",
    collected_at: collectedAt,
    checks: [
      {
        name: "example-check",
        severity: "hard",
        result: "pass",
        detail: "Reference mock check; always passes.",
      },
    ],
    metadata: {
      source: "reference-example",
    },
  };
}
