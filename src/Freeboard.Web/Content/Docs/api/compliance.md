# Compliance

Read the compliance model: the standards in play and how much of it is loaded.

These routes authenticate with a session token from `POST /auth/login`, sent as
`Authorization: Bearer <token>`. The browser session cookie is deliberately not accepted
on API routes.

Rows the caller has no access to are omitted rather than refused, so a short list is a
normal result rather than an error.
