## Context

Freeboard's authorization foundation already exists (per-org RBAC engine in
`Freeboard.Core/Authz`, enforcement and rollout wiring in `Freeboard/Authz`,
tables and seed in `Freeboard.Persistence` migration `010_authorization.sql`).
Epic 1 layers a paid feature on top: custom authorization policies. Paid features
need a gate that answers "may this install use this EE feature?".

Today there is no entitlement concept. The reference graph is strict and one-way:
`Freeboard.Core` references nothing and is MIT; `Freeboard.Enterprise` (proprietary)
may reference `Freeboard.Core` but never the reverse; `Freeboard.Agent` and
`Freeboard.CLI` must never reference `Freeboard.Enterprise`; only the web app
(`Freeboard`) combines Core, Enterprise, and Persistence.

The web app already has the pattern this change mirrors: `AuthzRuntimeOptions`
(`src/Freeboard/Authz/AuthzMode.cs`) reads a single config value
(`Authz:Mode`, via `builder.Configuration["Authz:Mode"]`) and is registered as a
singleton in `Program.cs`. The entitlement seam follows the same shape.

## Provenance

Two independent plans were merged into this change.

- Plan A (the original OpenSpec draft) contributed: the enum + single-method
  interface shape (D2), the raw-`IConfiguration` read that mirrors
  `AuthzRuntimeOptions` (D3), the "nothing consumes it, so nothing regresses"
  argument (D4), and the placement/architecture assertion.
- Plan B (Codex) contributed: the `Entitlements` folder/namespace name for the
  web implementation (resolving the "Enterprise folder reads as EE" concern
  outright rather than just mitigating it), and the service-provider resolution
  test using the real `AuthWebFactory` pattern.

The plans diverged on two points, resolved below: interface shape (D2) and how
config is read (D3). Both plans already agreed on placement (interface in Core,
config impl in the web app, nothing in `Freeboard.Enterprise`), the
`Enterprise:CustomPolicies` key, default-off via absent config + `false` bool
default, no `appsettings.json` entry, synchronous resolution, and the
one-DI-line extension-point model.

## Goals / Non-Goals

**Goals:**

- One MIT interface, consumable by any project (including the Core authz engine),
  that answers whether an install is entitled to a named EE feature.
- A config-backed implementation that defaults off, so an MIT build runs with the
  entitlement disabled and no EE assembly is required to answer the question.
- Extensibility to further entitlements by adding an enum member, with no
  interface or call-site churn.
- One documented extension point for a future license-key provider.

**Non-Goals:**

- License-key cryptography, signed tokens, or online activation (separate ticket).
- Any consumer of the entitlement. No feature is gated in this increment.
- Per-org/per-tenant entitlement scoping.

## Decisions

### D1. The interface lives in `Freeboard.Core` (MIT); the config implementation lives in the web app

The entitlement check is a gate, not a paid feature. MIT code must be able to ask
the question (the pure authz engine in `Freeboard.Core` is a plausible future
caller, and `Freeboard.Core` cannot reference `Freeboard.Enterprise`). So the
interface is MIT and lives in `Freeboard.Core`.

The config implementation just reads a boolean from `IConfiguration`. It is DI
plumbing, so it belongs where DI and config live: the web app. Placing it in
`Freeboard.Enterprise` was rejected - the EE project is reserved for actual paid
features, and putting the gate there would mean an MIT build could not resolve the
seam without the proprietary assembly. Placing the implementation in
`Freeboard.Core` was rejected too: `Freeboard.Core` has no dependency on
`Microsoft.Extensions.Configuration` and adding one to host a trivial reader is
unjustified liability for a base library shared by the Agent and CLI.

Result: interface in `src/Freeboard.Core/Enterprise/`, implementation in
`src/Freeboard/Entitlements/`, registered in `src/Freeboard/Program.cs`. The web
folder and namespace are `Entitlements` / `Freeboard.Entitlements`, not
`Enterprise`, so nothing in the MIT web project reads as EE carve-out code (see
Risks). This matches the existing folder-equals-namespace convention
(`src/Freeboard/Authz` -> `Freeboard.Authz`).

### D2. Interface shape: one method over an enum, not one property per feature (Plan A vs Plan B)

```csharp
// Freeboard.Core/Enterprise
public enum EnterpriseEntitlement
{
    CustomPolicies = 1,
}

public interface IEnterpriseEntitlements
{
    bool IsEntitled(EnterpriseEntitlement entitlement);
}
```

A single method keeps the gate to one call at every site
(`entitlements.IsEntitled(EnterpriseEntitlement.CustomPolicies)`) and makes adding
a future entitlement a one-line enum change with no interface edit and no
recompilation of existing callers. Plan B proposed the alternative - a
`bool CanUseCustomPolicies { get; }` property, adding one property per feature.
It is simpler for exactly one entitlement, but the ticket's stated intent is
"a CustomPolicies entitlement check, extendable to further entitlements later".
The enum shape serves that directly: the interface never changes and no call site
is re-plumbed as entitlements are added, whereas the property shape grows the
interface surface once per feature. So the enum + single method is chosen.

### D3. Config key: `Enterprise:CustomPolicies` (boolean), default off

The implementation maps each entitlement to a config key under an `Enterprise`
section and reads it as a boolean:

```
Enterprise:CustomPolicies = true   // entitled
(absent / false)                   // not entitled
```

Read with `configuration.GetValue<bool>("Enterprise:CustomPolicies")`, which
yields `false` when the key is absent. An unmapped entitlement returns `false`
(fail-safe, matching the authz engine's deny-by-default posture). The key is not
added to `appsettings.json`, so the default build is off and the file is unchanged.

The enum-to-key mapping is an explicit switch inside the implementation. Adding an
entitlement means adding the enum member and one switch arm; the default arm keeps
unknown entitlements off. Enum members are numbered from 1, so the default value 0
(`default(EnterpriseEntitlement)`) is unmapped and falls through the switch default
to `false`; any other unmapped or out-of-range value does the same.

Plan B proposed reading config through the options pattern instead:
`Configure<EnterpriseEntitlementOptions>(...GetSection("Enterprise"))` plus an
`IOptions<EnterpriseEntitlementOptions>` with a `CustomPolicies` bool property.
Both patterns exist in this codebase (`WebAuthOptions` uses `IOptions`;
`AuthzRuntimeOptions` reads `IConfiguration` directly). The raw-`IConfiguration`
read is chosen because it composes with the enum interface (D2): a bound options
object needs one property per entitlement, which reintroduces the exact
per-feature growth the enum shape avoids. Mirroring `AuthzRuntimeOptions` also
keeps the seam's config story identical to its nearest sibling.

### D4. Default off and "unaffected when off" come for free because nothing consumes the seam yet

This is a foundation ticket. The custom-policies feature and its gate call are
later tickets. With no call site, adding the interface, the implementation, and one
DI registration changes no existing behavior: seeded roles, migration `010`, and
the authz engine are untouched. The on/off unit tests exercise the implementation
directly; there is no runtime path to regress when the key is off.

### D5. Extension point: replace the single DI registration with a license provider

The only registration is one line in `Program.cs`:

```csharp
builder.Services.AddSingleton<IEnterpriseEntitlements, ConfigurationEnterpriseEntitlements>();
```

`ConfigurationEnterpriseEntitlements` takes `IConfiguration` via constructor
injection, which ASP.NET Core always registers, so type-based registration
resolves it without capturing the builder.

A future license-key ticket adds its own `IEnterpriseEntitlements` implementation
(for example in `Freeboard.Enterprise`, since license validation is a paid concern)
and swaps this one type-based registration. Callers are unaffected because they depend on the
interface. This is the documented extension point. No provider-selection abstraction,
factory, or options toggle is built now - that would be speculative for a single
current implementation.

## Risks / Trade-offs

- [A config-only gate is trivially bypassed by editing config] -> Accepted for
  this ticket. The scope explicitly excludes license-key crypto; a signed-token
  provider is the later hardening step and slots in via D5 without touching callers.
- [A folder inside the MIT web project could read as EE carve-out code] -> Resolved
  by naming: the web folder and namespace are `Entitlements` / `Freeboard.Entitlements`,
  with no "Enterprise" in the web project at all, so nothing collides with the EE
  project's `Freeboard.Enterprise`. A class-level comment states the file is MIT
  plumbing, not a paid feature. The architecture reference test continues to assert
  no community project references `Freeboard.Enterprise`.
- [A future entitlement added to the enum but not mapped in the switch silently
  returns false] -> Accepted as fail-safe. Default-off is the correct posture for a
  paid gate; a wrongly-on default would leak a paid feature.

## Migration Plan

Additive only. No migration, no data change, no config change to ship. Deploy is a
normal build. Rollback is removing the seam; since nothing consumes it, rollback is
inert. To enable custom policies once a later ticket wires the gate, set
`Enterprise:CustomPolicies` to `true` in the install's configuration.

## Open Questions

- Confirm the enum member name `CustomPolicies` and config key `Enterprise:CustomPolicies`
  match the naming the later EE tickets (T2, T3, T5, T6) expect to call. Renaming
  after those tickets land is a wider change.
