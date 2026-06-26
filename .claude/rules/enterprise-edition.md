# Enterprise Edition (EE) carve-outs

Freeboard follows a hybrid licensing model: the repo is MIT Expat by default,
with one proprietary carve-out directory.

## What is EE

- EE code lives only in `src/Freeboard.Enterprise`.
- That directory is licensed under `src/Freeboard.Enterprise/LICENSE`, not MIT.
- Everything outside it is MIT Expat (see root `LICENSE`).

## Rules

- Default to MIT. Put code in `src/Freeboard.Enterprise` only when it is a paid,
  enterprise-gated feature. If unsure, it is not EE.
- Dependency direction is one-way:
  - `Freeboard.Enterprise` may reference `Freeboard.Core`.
  - `Freeboard.Core`, `Freeboard.Agent`, and `Freeboard.CLI` must never reference
    `Freeboard.Enterprise`. Keep these MIT and EE-free.
  - The web app (`Freeboard`) is the only component that combines Core and EE.
- Never copy or move EE code into an MIT project. That relicenses proprietary code.
- New MIT-licensed features go in `Freeboard.Core` or the relevant non-EE project.
