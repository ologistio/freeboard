## 1. Core entitlement seam (feat(core): enterprise entitlement seam)

- [x] 1.1 Add `src/Freeboard.Core/Enterprise/EnterpriseEntitlement.cs`: a public
  `EnterpriseEntitlement` enum with one member, `CustomPolicies = 1`. Value 0 is
  reserved as unmapped/none so that `default(EnterpriseEntitlement)` falls through
  the implementation's switch default and returns false. Namespace
  `Freeboard.Core.Enterprise`.
- [x] 1.2 Add `src/Freeboard.Core/Enterprise/IEnterpriseEntitlements.cs`: a public
  interface with one method, `bool IsEntitled(EnterpriseEntitlement entitlement)`.
  Keep it pure - no web, persistence, or `Freeboard.Enterprise` reference. Add a
  short doc comment stating it is the MIT gate that decides EE-feature entitlement.
- [x] 1.3 Verify: `dotnet build src/Freeboard.Core`.

## 2. Config-backed implementation and DI (feat(web): config-backed enterprise entitlements)

- [x] 2.1 Add `src/Freeboard/Entitlements/ConfigurationEnterpriseEntitlements.cs`:
  a sealed class implementing `IEnterpriseEntitlements`, with a constructor taking
  `IConfiguration` via DI (ASP.NET Core always registers `IConfiguration`).
  Namespace `Freeboard.Entitlements` (no "Enterprise" in the web project, so it does
  not read as EE carve-out code; matches the `src/Freeboard/Authz` ->
  `Freeboard.Authz` convention). Map each entitlement to its config key with a switch:
  `CustomPolicies` reads `configuration.GetValue<bool>("Enterprise:CustomPolicies")`;
  the default arm returns false (fail-safe). Add a class comment noting this is MIT
  DI plumbing, not a paid feature, and that a future license provider replaces the
  single DI registration (the documented extension point).
- [x] 2.2 Register the seam in `src/Freeboard/Program.cs` next to the authz wiring:
  `builder.Services.AddSingleton<IEnterpriseEntitlements, ConfigurationEnterpriseEntitlements>();`.
  Do not add an `Enterprise` section to `appsettings.json` (default off).
- [x] 2.3 Verify: `dotnet build src/Freeboard`.

## 3. Tests (test(web): entitlement on/off and placement)

- [x] 3.1 Add `tests/Freeboard.Web.Tests/EnterpriseEntitlementsTests.cs`: construct
  `ConfigurationEnterpriseEntitlements` with an in-memory `IConfiguration` (mirror
  `EmailRegistrationTests`). Cover: absent config -> `IsEntitled(CustomPolicies)`
  false; `Enterprise:CustomPolicies=false` -> false; `Enterprise:CustomPolicies=true`
  -> true; the default/zero value `IsEntitled((EnterpriseEntitlement)0)` -> false;
  an out-of-range value `IsEntitled((EnterpriseEntitlement)999)` -> false (pins the
  switch default arm). Use built-in `Assert.*`.
- [x] 3.2 Add a service-provider resolution test in `tests/Freeboard.Web.Tests`
  using the real `AuthWebFactory` pattern (see `AuthWebFactory.cs`): resolve
  `IEnterpriseEntitlements` from the built provider and assert it is the
  config-backed implementation whose assembly is `Freeboard`. Optionally assert
  that `UseSetting("Enterprise:CustomPolicies", "true")` flips
  `IsEntitled(CustomPolicies)` to true through the resolved service. Satisfies the
  "Resolvable from the web app service provider" scenario.
- [x] 3.3 Add a placement assertion (in
  `tests/Freeboard.Architecture.Tests/AuthzPlacementTests.cs` or a new
  `EntitlementPlacementTests.cs`): assert
  `typeof(IEnterpriseEntitlements).Assembly.GetName().Name == "Freeboard.Core"`.
- [x] 3.4 Verify: `dotnet build` and `dotnet test tests/Freeboard.Web.Tests
  tests/Freeboard.Architecture.Tests tests/Freeboard.Core.Tests`. Confirm existing
  `EnterpriseReferenceTests` and `AuthzPlacementTests` stay green. Confirm the
  existing web authz behavioral suite in `tests/Freeboard.Web.Tests`
  (`AdminPageAuthzTests`, `SessionRouteAuthzTests`, `AuthorizerTests`) stays green:
  it boots `Program.cs` with the seam registered, so its unchanged results are the
  regression guard proving authorization decisions are identical when the seam is
  off (traces the "Seam does not disturb the authorization foundation when off"
  requirement). No new authz test is added.
