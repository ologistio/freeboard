# Freeboard

## GitOps config management

Freeboard manages compliance state (standards, controls, scopes) as declarative
YAML in git. The `freeboard gitops` CLI validates and previews config, and the
web app can run read-only so changes flow through git.

```sh
freeboard gitops validate examples/gitops
freeboard gitops apply examples/gitops --dry-run
```

Compliance state persists in MySQL. Apply the schema, then sync a validated
config into the store; the web app serves the persisted domain read-only.

```sh
docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml up -d
export FREEBOARD_DB="Server=127.0.0.1;Port=3306;Database=freeboard;User ID=freeboard;Password=freeboard;"
freeboard system migrate
freeboard gitops sync examples/gitops
```

`gitops sync` hard-removes resources dropped from the config. The connection
string is a secret: use `FREEBOARD_DB`, user-secrets, or a config provider, never
git.

Applying migration 011 (and later trigger-using migrations) runs `CREATE TRIGGER`, so
on a binary-logging MySQL 8.x server the migration user must run against a server with
`log_bin_trust_function_creators=1` or hold a privilege sufficient to create triggers
under binary logging; otherwise `system migrate` fails with error 1419.

See [docs/gitops.md](docs/gitops.md) for the format, commands, persistence, and
read-only mode. See [examples/gitops/](examples/gitops/) for a sample config.
