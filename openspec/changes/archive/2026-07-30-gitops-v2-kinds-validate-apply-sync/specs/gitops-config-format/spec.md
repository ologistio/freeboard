## ADDED Requirements

### Requirement: The shipped example config exercises every supported kind

The generic example config directory (`examples/gitops`) SHALL contain at least one
document of every kind the loader and validator support - `Standard`, `Requirement`,
`Control`, `Asset`, `Scope`, `Collector`, and `Integration` - so the worked sample the
project documentation points operators at matches the documented kind set. Its README
layout table SHALL name every file and the kinds it carries.

Every shipped example config root - the directories the project documentation tells an
operator to run the command against, today `examples/gitops` and `examples/fixture-corp` -
SHALL validate clean: `freeboard gitops validate` over it SHALL exit `0` with no error
diagnostics. Continuous integration SHALL run that command over each of those roots, so an
example edited into an invalid state fails the build rather than reaching an operator.

A shared catalog fragment that example roots symlink in (today
`examples/shared/cyber-essentials-plus.yaml`, the Cyber Essentials Plus standard and its
requirements) is NOT a config root and SHALL NOT be validated as one: it is authored to be
composed with a company's own documents, so it carries no obligation to be complete on its
own. It is already covered because each root that symlinks it loads it.

#### Scenario: Generic example carries every kind

- **WHEN** the documents under `examples/gitops` are loaded
- **THEN** the loaded config contains at least one `Standard`, `Requirement`, `Control`,
  `Asset`, `Scope`, `Collector`, and `Integration`

#### Scenario: Example README layout table matches the directory

- **WHEN** a reader consults the layout table in `examples/gitops/README.md`
- **THEN** every YAML file in the directory has a row, and the kinds named in each row are
  the kinds that file carries, including the collector and integration files

#### Scenario: Continuous integration validates every shipped example config root

- **WHEN** the build runs
- **THEN** it runs `freeboard gitops validate` over each shipped example config root
  (`examples/gitops` and `examples/fixture-corp`) and fails if any exits non-zero, while
  the shared catalog fragment under `examples/shared` is validated only through the roots
  that symlink it
