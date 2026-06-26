# Contributing to Freeboard

## Commit messages

All commits must follow [Conventional Commits v1.0.0](https://www.conventionalcommits.org/en/v1.0.0/#specification).

### Format

```plaintext
<type>[optional scope]: <description>

[optional body]

[optional footer(s)]
```

- Use the imperative mood in the description ("add", not "added").
- Keep the description short; put detail in the body.
- Separate the body and each footer with a blank line.

### Types

- `feat`: a new feature (triggers a MINOR release).
- `fix`: a bug fix (triggers a PATCH release).
- `build`, `chore`, `ci`, `docs`, `perf`, `refactor`, `style`, `test`: other
  changes that do not affect the public API.

### Scope

Optional noun in parentheses naming the affected area, usually a project:
`core`, `enterprise`, `agent`, `cli`, `web`. Example: `feat(cli): ...`.

### Breaking changes

A commit is a breaking change (triggers a MAJOR release) if either:

- a `!` is placed before the colon: `feat(core)!: drop legacy config format`, or
- a footer begins with `BREAKING CHANGE:` followed by a description.

If both are used, the `!` still requires the `BREAKING CHANGE:` footer to carry
the description.

### Examples

```plaintext
feat(agent): collect disk usage metrics
```

```plaintext
fix(cli): exit non-zero when config file is missing
```

```plaintext
refactor(core)!: rename FreeboardInfo to ProductInfo

BREAKING CHANGE: FreeboardInfo is removed. Use ProductInfo instead.
```

## Versioning

Releases follow [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html):
`MAJOR.MINOR.PATCH`.

- `MAJOR`: incompatible (breaking) public API changes.
- `MINOR`: backwards-compatible new functionality.
- `PATCH`: backwards-compatible bug fixes.

The public API is the surface other components rely on: exported types and
members in `Freeboard.Core` and `Freeboard.Enterprise`, the CLI commands and
flags, the agent's data and config formats, and the web app's HTTP endpoints.

Commit types map to version bumps:

- `fix` -> PATCH.
- `feat` -> MINOR.
- any commit with `!` or a `BREAKING CHANGE:` footer -> MAJOR.

Extra rules:

- A pre-release uses a suffix: `1.4.0-rc.1`.
- Before `1.0.0` (`0.y.z`), the API is unstable and any release may break it.
- Once a version is published, its contents must not change. Fix issues with a
  new version.

## Markdown

All Markdown files must pass [markdownlint](https://github.com/DavidAnson/markdownlint).
The shared rule set is `.markdownlint.jsonc` at the repo root: markdownlint
defaults, with line-length (`MD013`) disabled.

Run it before committing:

```sh
npx markdownlint-cli2 "**/*.md"
```

Fix reported issues rather than disabling rules. Suppress a rule inline only when
a specific line genuinely needs it, using a `<!-- markdownlint-disable-line -->`
comment.

One carveout: the OpenSpec CLI generates instruction files under
`.claude/skills/openspec-*/` and `.claude/commands/opsx/`. The CLI regenerates
them, so they are excluded from linting via `.markdownlint-cli2.jsonc`. Do not
hand-edit them or extend the carveout to other paths.

## Releases

Releases are automated with GitHub Actions. The flow:

1. Open a PR with a Conventional Commit title. `release-drafter` autolabels it
   (`feat`, `fix`, `chore`, `breaking`) from that title.
2. On merge to `main`:
   - `release-drafter` updates the draft release notes and the next version,
     resolved from the merged labels (`breaking` -> MAJOR, `feat` -> MINOR,
     `fix` -> PATCH).
   - `build` produces a snapshot build (version
     `0.0.0-snapshot.<run>+<sha>`) and uploads it as an artifact.
3. To cut a release, edit the draft release in GitHub and publish it. Publishing
   triggers `build` again, producing a versioned build from the release tag
   (`vX.Y.Z`).

Config and workflows:

- `.github/release-drafter.yml` - categories, version resolver, autolabeler.
- `.github/workflows/release-drafter.yml` - drafts releases and labels PRs.
- `.github/workflows/build.yml` - snapshot and versioned builds.
