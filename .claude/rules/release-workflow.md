# Release workflow

Releases are automated with `release-drafter` and GitHub Actions.

- PRs are autolabeled from their Conventional Commit title; labels drive the next
  SemVer version.
- Merging to `main` updates the draft release and produces a snapshot build
  artifact (`0.0.0-snapshot.<run>+<sha>`).
- Publishing a draft release produces a versioned build from its `vX.Y.Z` tag.

Edit builds and notes in `.github/workflows/build.yml`,
`.github/workflows/release-drafter.yml`, and `.github/release-drafter.yml`.

See `CONTRIBUTING.md` for the full flow.
