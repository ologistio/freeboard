## Why

Epic 1 introduces the first paid Enterprise Edition (EE) authorization feature:
custom authorization policies. Before any EE feature can decide whether it is
allowed to run, the app needs one place to ask "is this install entitled to a
given paid feature?". No entitlement concept exists yet. This ticket adds that
seam - and nothing else - so the later EE tickets (T2, T3, T5, T6) have a single,
stable gate to call instead of each inventing its own check.

This change is MIT (default), not an EE carve-out. The entitlement check is the
gate that decides EE access; it is not itself a paid feature, and MIT code
(including the pure authorization engine in `Freeboard.Core`) must be able to
consume it. The interface therefore lives in `Freeboard.Core`. The config-backed
implementation is DI plumbing and lives in the web app. No code lands in
`Freeboard.Enterprise`; that project stays reserved for the paid features the
seam will later gate.

## What Changes

- Add `IEnterpriseEntitlements` in `Freeboard.Core` (MIT): one method,
  `IsEntitled(EnterpriseEntitlement entitlement)`, plus an `EnterpriseEntitlement`
  enum whose only member for now is `CustomPolicies`. New entitlements are added
  later by adding an enum member (additive, no interface change).
- Add a config-backed implementation in the web app (`Freeboard`) that reads the
  `Enterprise:CustomPolicies` boolean from `IConfiguration`. It is registered as a
  singleton in `Program.cs`. Default off: an absent or false key means not
  entitled, and any unmapped entitlement is not entitled (fail-safe).
- Document one extension point: swapping the single DI registration for a future
  license-provider implementation. No provider-selection framework is built now.
- Add unit tests covering the on and off states of `CustomPolicies`.

Nothing consumes the entitlement in this increment (the custom-policies feature
is a later ticket), so no existing code path changes. This is a pure addition of
a seam.

## Capabilities

### New Capabilities

- `enterprise-entitlements`: the single entitlement seam - the MIT interface and
  entitlement enum in `Freeboard.Core`, the config-backed web implementation, its
  default-off behavior, and the documented license-provider extension point.

### Modified Capabilities

None. No existing requirement changes. The seam has no call sites yet, so seeded
roles and the authz foundation are unaffected.

## Impact

- New code: `Freeboard.Core` gains an `Enterprise` namespace with
  `IEnterpriseEntitlements` and `EnterpriseEntitlement`. The web app gains one
  config-backed implementation class and one DI registration line in `Program.cs`.
- Config: a new optional `Enterprise:CustomPolicies` boolean key. Absent by
  default, so `appsettings.json` is unchanged and the feature is off out of the box.
- Tests: new on/off unit tests in `Freeboard.Web.Tests`; a placement assertion
  pins the interface to the `Freeboard.Core` assembly.
- MIT vs EE: entirely MIT. Nothing added to `Freeboard.Enterprise`. `Freeboard.Agent`
  and `Freeboard.CLI` gain no reference to `Freeboard.Enterprise` and need not wire
  the web-only implementation; the MIT interface is present transitively via
  `Freeboard.Core`, which is expected and not a violation.

## Non-goals

- Signed license keys or entitlement tokens, and any license-key cryptography.
  That is a separate later ticket. This seam is config-driven only.
- Consuming the entitlement anywhere (gating an actual feature). The custom
  authorization policies feature and its gate call are later tickets (T2+).
- Per-organisation or per-tenant entitlement resolution. The seam answers a single
  install-wide question for now.
