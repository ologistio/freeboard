# Evidence

Collectors post the checks they ran; Freeboard records the run and counts the failures.

Ingest authenticates with a collector credential, not a user session. Issue one with
`POST /collectors/{id}/credentials`; the token comes back exactly once. A revoked or
expired credential authenticates but is refused with 403, an unknown one with 401.

Ingest is idempotent on the run: posting a run that has already been recorded returns
200 with the original counts rather than recording it twice.
