# Freeboard

## GitOps config management

Freeboard manages compliance state (standards, controls, scopes) as declarative
YAML in git. The `freeboard gitops` CLI validates and previews config, and the
web app can run read-only so changes flow through git.

```sh
freeboard gitops validate examples/gitops
freeboard gitops apply examples/gitops --dry-run
```

See [docs/gitops.md](docs/gitops.md) for the format, commands, and read-only
mode. See [examples/gitops/](examples/gitops/) for a sample config.
