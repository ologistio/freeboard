using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The compliance read endpoints (standards, controls, organisations, scopes, statement-of-
/// applicability, compliance/status) require an authenticated user: any logged-in user reads,
/// admin is NOT required, and an anonymous caller is 401'd. Once authenticated, the store-
/// unreachable degradation stands (503 on the resource reads, 200 all-null for status).
/// </summary>
public sealed class ComplianceEndpointTests
{
    private static readonly string[] ResourceReadPaths =
    [
        "/api/v1/freeboard/standards",
        "/api/v1/freeboard/requirements",
        "/api/v1/freeboard/controls",
        "/api/v1/freeboard/organisations",
        "/api/v1/freeboard/scopes",
        "/api/v1/freeboard/vendors",
        "/api/v1/freeboard/collectors",
        "/api/v1/freeboard/statement-of-applicability/std-a",
    ];

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Standards =
        [
            new StandardRow("std-a", "Standard A", "1.0", "Example Authority", "Example Publisher", "https://example.com/std-a"),
            new StandardRow("std-b", "Standard B", "2.0", "Other Authority", null, null),
        ],
        Requirements =
        [
            new RequirementRow("req-a", "Requirement A", "std-a", "Theme A", "Do the thing.", null, "Source A", "https://example.com/a"),
            new RequirementRow("req-b", "Requirement B", "std-a", "Theme A", "Do the other thing.", "Some guidance.", "Source B", "https://example.com/b"),
        ],
        Controls = [new ControlRow("ctrl-a", "Control A", ["req-a", "req-b"], "all")],
        Assets =
        [
            TestAssets.Org("org-a", title: "Org A"),
            TestAssets.Org("org-eng", "org-a", "Department", "Engineering"),
            TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A"),
            TestAssets.Vendor("vendor-b", "org-a", title: "Vendor B"),
        ],
        // One unified scope set: a standard-target and two requirement-target org scopes, plus two
        // vendor-subject scopes. Readability resolves each Subject against the asset set; SoA
        // resolution reads the same list.
        Scopes =
        [
            new ScopeRow("scope-a", "Scope A", "org-a", "std-a", null, null, "In", null),
            new ScopeRow("rs-a", "Exclude req-a", "org-a", null, "req-a", null, "Out", "req-a excluded"),
            new ScopeRow("rs-b", "Exclude req-b", "org-a", null, "req-b", null, "Out", "req-b excluded"),
            new ScopeRow("vs-a", "Except req-a for vendor-a", "vendor-a", null, "req-a", null, "Out", "Supports MFA but not SSO."),
            new ScopeRow("vs-b", "Include ctrl-a for vendor-a", "vendor-a", null, null, "ctrl-a", "In", null),
        ],
        Collectors =
        [
            new CollectorRow(
                "collector-a", "Endpoint MFA", "ctrl-a", "vendor-a", "integration", "fleet", "daily", 100,
                new CollectorConfigView(
                    null, [], null, [], [new Check { SourceKey = "12", Name = "mfa-enforced", Severity = "Hard" }])),
            new CollectorRow(
                "collector-script", "Nightly script", "ctrl-a", null, "script", null, "daily", null,
                CollectorConfigView.Empty),
            new CollectorRow(
                "attest-manual", "Firewall attestation", "ctrl-a", null, "manual", null, "annual", null,
                new CollectorConfigView(
                    "Confirm review.",
                    [new AttestationField { Id = "reviewed", Label = "Ruleset reviewed?", Type = "boolean" }],
                    null, [], [])),
            new CollectorRow(
                "attest-training", "Phishing awareness", "ctrl-a", null, "training", null, "annual", null,
                new CollectorConfigView(
                    null, [], 80, [new QuizItemView("q1", "What should you do?", ["Open it", "Report it"])], [])),
        ],
    };

    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false)
        => new() { Compliance = store, ReadOnly = readOnly };

    /// <summary>An authenticated non-admin (member) user; reads are not admin-gated.</summary>
    private static HttpClient MemberClient(AuthWebFactory factory)
        => factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("member1"));

    [Fact]
    public async Task StandardsEndpointReturnsIdsAndTitlesOrderedById()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/standards");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("std-a", json[0].GetProperty("id").GetString());
        Assert.Equal("Standard A", json[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task StandardsEndpointIncludesMetadataFields()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/standards");

        var stdA = json[0];
        Assert.Equal("1.0", stdA.GetProperty("version").GetString());
        Assert.Equal("Example Authority", stdA.GetProperty("authority").GetString());
        Assert.Equal("Example Publisher", stdA.GetProperty("publisher").GetString());
        Assert.Equal("https://example.com/std-a", stdA.GetProperty("source_url").GetString());

        // Unset optional metadata serializes as null.
        var stdB = json[1];
        Assert.Equal(JsonValueKind.Null, stdB.GetProperty("publisher").ValueKind);
        Assert.Equal(JsonValueKind.Null, stdB.GetProperty("source_url").ValueKind);
    }

    [Fact]
    public async Task RequirementsEndpointReturnsFieldsAndComposedCitationOrderedById()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/requirements");

        Assert.Equal(2, json.GetArrayLength());
        var first = json[0];
        Assert.Equal("req-a", first.GetProperty("id").GetString());
        Assert.Equal("std-a", first.GetProperty("standard").GetString());
        Assert.Equal("Theme A", first.GetProperty("theme").GetString());
        Assert.Equal("Do the thing.", first.GetProperty("statement").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("guidance").ValueKind);

        // citation_label/citation_url are composed into a nested citation object.
        var citation = first.GetProperty("citation");
        Assert.Equal("Source A", citation.GetProperty("label").GetString());
        Assert.Equal("https://example.com/a", citation.GetProperty("url").GetString());

        // Ordered by id: req-a then req-b; req-b carries its guidance.
        Assert.Equal("req-b", json[1].GetProperty("id").GetString());
        Assert.Equal("Some guidance.", json[1].GetProperty("guidance").GetString());
    }

    [Fact]
    public async Task ControlsEndpointReturnsResolvedMapsTo()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/controls");

        var control = json[0];
        Assert.Equal("ctrl-a", control.GetProperty("id").GetString());
        var mapsTo = control.GetProperty("maps_to").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["req-a", "req-b"], mapsTo);
        Assert.Equal("all", control.GetProperty("evaluation").GetString());
    }

    [Fact]
    public async Task ControlsEndpointNullsEvaluationWhenUnset()
    {
        var store = PopulatedStore();
        store.Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], null)];
        using var factory = Factory(store);
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/controls");

        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("evaluation").ValueKind);
    }

    [Fact]
    public async Task CollectorsEndpointReturnsEveryRowWithItsTypedConfig()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/collectors");

        Assert.Equal(4, json.GetArrayLength());

        var integration = json[0];
        Assert.Equal("collector-a", integration.GetProperty("id").GetString());
        Assert.Equal("ctrl-a", integration.GetProperty("control").GetString());
        Assert.Equal("vendor-a", integration.GetProperty("vendor").GetString());
        Assert.Equal("integration", integration.GetProperty("type").GetString());
        Assert.Equal("fleet", integration.GetProperty("provider").GetString());
        Assert.Equal("daily", integration.GetProperty("frequency").GetString());
        Assert.Equal(100, integration.GetProperty("threshold").GetInt32());
        var check = integration.GetProperty("config").GetProperty("checks")[0];
        Assert.Equal("12", check.GetProperty("source_key").GetString());
        Assert.Equal("mfa-enforced", check.GetProperty("name").GetString());
        Assert.Equal("Hard", check.GetProperty("severity").GetString());

        var manual = json[2];
        Assert.Equal("manual", manual.GetProperty("type").GetString());
        Assert.Equal("Confirm review.", manual.GetProperty("config").GetProperty("body").GetString());
        var field = manual.GetProperty("config").GetProperty("fields")[0];
        Assert.Equal("reviewed", field.GetProperty("id").GetString());
        Assert.Equal("boolean", field.GetProperty("type").GetString());

        var training = json[3];
        Assert.Equal("training", training.GetProperty("type").GetString());
        Assert.Equal(80, training.GetProperty("config").GetProperty("pass_mark").GetInt32());
        var item = training.GetProperty("config").GetProperty("quiz")[0];
        Assert.Equal("q1", item.GetProperty("id").GetString());
        Assert.Equal("What should you do?", item.GetProperty("prompt").GetString());
        Assert.Equal(["Open it", "Report it"], item.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToArray());
    }

    // A config key is written only when its member carries a value, so `config` holds exactly the keys the
    // collector's schema can register - never five keys with three nulls.
    [Fact]
    public async Task CollectorConfigOmitsEveryAbsentMember()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/collectors");

        // A script collector registers no config key at all.
        var script = json[1].GetProperty("config");
        Assert.Equal(JsonValueKind.Object, script.ValueKind);
        Assert.Empty(script.EnumerateObject());

        // A training collector carries no checks key; an integration one carries no attestation key.
        var training = json[3].GetProperty("config");
        Assert.False(training.TryGetProperty("checks", out _));
        Assert.False(training.TryGetProperty("body", out _));
        var integration = json[0].GetProperty("config");
        Assert.Equal(["checks"], integration.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // The omission stops at `config`: a top-level member every collector has stays an explicit null.
    [Fact]
    public async Task TopLevelNullablesStayExplicitNull()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/collectors");

        var script = json[1];
        Assert.Equal(JsonValueKind.Null, script.GetProperty("vendor").ValueKind);
        Assert.Equal(JsonValueKind.Null, script.GetProperty("provider").ValueKind);
        Assert.Equal(JsonValueKind.Null, script.GetProperty("threshold").ValueKind);
    }

    [Fact]
    public async Task CollectorsEndpointNeverExposesQuizAnswer()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var raw = await client.GetStringAsync("/api/v1/freeboard/collectors");
        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        var item = json[3].GetProperty("config").GetProperty("quiz")[0];

        Assert.False(item.TryGetProperty("answer", out _));
        // Redaction is proved by the absence of the `answer` key, not by the absence of the answer's
        // text: the correct answer is legitimately present in the JSON as one of the quiz options.
        Assert.DoesNotContain("answer", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectorsReadServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/freeboard/collectors")).StatusCode);
    }

    [Fact]
    public async Task CollectorsEndpointReturns503WhenStoreUnreachable()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/collectors");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ZeroGrantEnforceCallerStillReadsEveryCollector()
    {
        // The collectors endpoint does NOT narrow by IAssetAccess. Under strict Enforce with no grants a
        // member still reads every collector.
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/collectors");
        Assert.Equal(
            ["collector-a", "collector-script", "attest-manual", "attest-training"],
            json.EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public async Task CollectorsEndpointReturnsEveryRowWithAnUnreadableVendorNulled()
    {
        // The vendor id names a vendor asset the owner edge governs, so it is withheld from a caller who
        // cannot read it - but the ROW stays, because collectors carry no organisation dimension.
        var store = PopulatedStore();
        store.Assets = [TestAssets.Org("org-a"), TestAssets.Org("org-x"), TestAssets.Vendor("vendor-a", "org-x")];
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/collectors");

        Assert.Equal(4, json.GetArrayLength());
        Assert.All(json.EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("vendor").ValueKind));
        Assert.DoesNotContain("vendor-a", (await client.GetStringAsync("/api/v1/freeboard/collectors")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectorsEndpointKeepsAReadableVendorId()
    {
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/collectors");

        Assert.Equal("vendor-a", json.EnumerateArray().Single(c => c.GetProperty("id").GetString() == "collector-a")
            .GetProperty("vendor").GetString());
    }

    // The credential routes are exercised as an admin, so a route that came back would answer rather
    // than 403 and the 404 assertion would bite.
    [Theory]
    [InlineData("GET", "/api/v1/freeboard/evidence-collectors")]
    [InlineData("GET", "/api/v1/freeboard/attestation-templates")]
    [InlineData("POST", "/api/v1/freeboard/evidence-collectors/collector-a/credentials")]
    [InlineData("DELETE", "/api/v1/freeboard/evidence-collectors/collector-a/credentials/cred-1")]
    public async Task RetiredRoutesAreUnmapped(string method, string retired)
    {
        using var factory = Factory(PopulatedStore());
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin1", role: "admin"));

        using var request = new HttpRequestMessage(new HttpMethod(method), retired);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task OrganisationsEndpointReturnsTreeWithKindAndParent()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/organisations");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("org-a", json[0].GetProperty("id").GetString());
        Assert.Equal("Company", json[0].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("parent").ValueKind);
        Assert.Equal("org-eng", json[1].GetProperty("id").GetString());
        Assert.Equal("org-a", json[1].GetProperty("parent").GetString());
    }

    [Fact]
    public async Task OrganisationsEndpointOmitsEveryNonOrganisationAssetAndKeepsParentOrganisationOnly()
    {
        // The listing is served from the one asset read, so its type filter and its parent field must
        // both stay organisation-only: parent always names a row of this same listing, or null.
        var store = PopulatedStore();
        store.Assets =
        [
            TestAssets.Org("org-a"), TestAssets.Machine("m-1", "org-a"),
            TestAssets.Org("org-under-machine", "m-1"), TestAssets.Vendor("vendor-a", "org-a"),
        ];
        using var factory = Factory(store);
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/organisations");

        var ids = json.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["org-a", "org-under-machine"], ids);
        // m-1 is readable to this caller, but it is not an organisation, so it must not surface here.
        var nested = json.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "org-under-machine");
        Assert.Equal(JsonValueKind.Null, nested.GetProperty("parent").ValueKind);
    }

    [Fact]
    public async Task VendorsEndpointPublishesTheRegisterRowAndNoOwner()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/vendors");

        // The row mirrors the register. The owner edge is the authorization anchor and is deliberately
        // not published.
        Assert.Equal(
            ["id", "title", "tier", "data_classes", "assurances"],
            json[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task VendorsEndpointReturnsEachAssuranceWithItsDerivedStatus()
    {
        // The endpoint derives the status rather than returning the expiry alone, so the warning window
        // lives in one process and the page and the CLI cannot disagree about one certification.
        var store = PopulatedStore();
        store.Assurances =
        [
            new VendorAssuranceRow("vendor-a", "std-a", AssuranceToday.AddDays(365), null),
            new VendorAssuranceRow("vendor-a", "std-b", AssuranceToday.AddDays(5), null),
            new VendorAssuranceRow("vendor-b", "std-a", AssuranceToday.AddDays(-3), null),
        ];
        using var factory = new AuthWebFactory { Compliance = store, Clock = new FixedClock(AssuranceToday) };
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/vendors");

        var vendorA = json.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "vendor-a");
        var assurances = vendorA.GetProperty("assurances").EnumerateArray().ToList();
        Assert.Equal(2, assurances.Count);
        Assert.Equal("std-a", assurances[0].GetProperty("standard").GetString());
        Assert.Equal(AssuranceToday.AddDays(365).ToString("yyyy-MM-dd"), assurances[0].GetProperty("expires").GetString());
        Assert.Equal("Valid", assurances[0].GetProperty("status").GetString());
        Assert.Equal("Expiring", assurances[1].GetProperty("status").GetString());

        var vendorB = json.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "vendor-b");
        Assert.Equal("Expired", vendorB.GetProperty("assurances")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task VendorsEndpointOmitsAHiddenVendorsAssurances()
    {
        // The row itself is absent rather than redacted, so neither the vendor id nor its assurance
        // reaches the caller.
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a"),
                TestAssets.Org("org-b"),
                TestAssets.Vendor("vendor-a", "org-a"),
                TestAssets.Vendor("vendor-b", "org-b"),
            ],
            Assurances = [new VendorAssuranceRow("vendor-b", "std-hidden", AssuranceToday.AddDays(5), null)],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory
        {
            Compliance = store,
            AuthzMode = "Enforce",
            Authz = authz,
            Clock = new FixedClock(AssuranceToday),
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.GetAsync("/api/v1/freeboard/vendors");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("vendor-b", body, StringComparison.Ordinal);
        Assert.DoesNotContain("std-hidden", body, StringComparison.Ordinal);
        Assert.Contains("vendor-a", body, StringComparison.Ordinal);
    }

    private static readonly DateOnly AssuranceToday = new(2026, 3, 1);

    private sealed class FixedClock(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    [Fact]
    public async Task ScopesEndpointReturnsUnifiedRow()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");

        var scope = json.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "scope-a");
        Assert.Equal("org-a", scope.GetProperty("subject").GetString());
        Assert.Equal("std-a", scope.GetProperty("standard").GetString());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("requirement").ValueKind);
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("control").ValueKind);
        Assert.Equal("In", scope.GetProperty("disposition").GetString());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("justification").ValueKind);

        // A requirement-target row carries its requirement id and null standard/control.
        var rs = json.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "rs-a");
        Assert.Equal("req-a", rs.GetProperty("requirement").GetString());
        Assert.Equal(JsonValueKind.Null, rs.GetProperty("standard").ValueKind);

        // The response projects only the eight public fields; no subject narrowing fields leak.
        var raw = await client.GetStringAsync("/api/v1/freeboard/scopes");
        Assert.DoesNotContain("subjectType", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subjectOwner", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subjectParent", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovedRequirementScopesReadEndpointIs404()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/freeboard/requirement-scopes")).StatusCode);
    }

    [Fact]
    public async Task RemovedVendorScopesReadEndpointIs404()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/freeboard/vendor-scopes")).StatusCode);
    }

    [Fact]
    public async Task VendorsEndpointReturnsIdsAndTitlesOrderedById()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/vendors");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("vendor-a", json[0].GetProperty("id").GetString());
        Assert.Equal("Vendor A", json[0].GetProperty("title").GetString());
        Assert.Equal("vendor-b", json[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ScopesEndpointReturnsVendorSubjectRowsWithTargetsAndJustifications()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");

        // vs-a: vendor subject, requirement target, Out, justification present. control null.
        var vsA = json.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "vs-a");
        Assert.Equal("vendor-a", vsA.GetProperty("subject").GetString());
        Assert.Equal("req-a", vsA.GetProperty("requirement").GetString());
        Assert.Equal(JsonValueKind.Null, vsA.GetProperty("control").ValueKind);
        Assert.Equal("Out", vsA.GetProperty("disposition").GetString());
        Assert.Equal("Supports MFA but not SSO.", vsA.GetProperty("justification").GetString());

        // vs-b: vendor subject, control target, In, no justification. requirement null.
        var vsB = json.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "vs-b");
        Assert.Equal("ctrl-a", vsB.GetProperty("control").GetString());
        Assert.Equal(JsonValueKind.Null, vsB.GetProperty("requirement").ValueKind);
        Assert.Equal("In", vsB.GetProperty("disposition").GetString());
        Assert.Equal(JsonValueKind.Null, vsB.GetProperty("justification").ValueKind);
    }

    [Fact]
    public async Task VendorReadsServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/freeboard/vendors")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/freeboard/scopes")).StatusCode);
    }

    [Fact]
    public async Task OwnerExcludedEnforceCallerSeesNoVendorOrScope()
    {
        // /scopes narrows a vendor subject by its owner. Under strict Enforce with no grants the
        // accessible-org set is empty, so every vendor is hidden - and with it every vendor-subject
        // scope, so an Out justification for a hidden vendor never leaks.
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var vendors = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/vendors");
        Assert.Equal(0, vendors.GetArrayLength());

        var scopes = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");
        Assert.Equal(0, scopes.GetArrayLength());

        var raw = await client.GetStringAsync("/api/v1/freeboard/scopes");
        Assert.DoesNotContain("Supports MFA but not SSO.", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopeReadsNarrowToTheCallerAccessibleOwners()
    {
        // Two vendors owned by different orgs; the reader is granted on org-a only. Under Enforce the
        // caller sees vendor-a's scope, but neither vendor-b's scope nor its Out justification - the
        // hidden vendor subject fails closed.
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a", title: "Org A"),
                TestAssets.Org("org-b", title: "Org B"),
                TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A"),
                TestAssets.Vendor("vendor-b", "org-b", title: "Vendor B"),
            ],
            Scopes =
            [
                new ScopeRow("vs-a", "Except req-a for vendor-a", "vendor-a", null, "req-a", null, "In", null),
                new ScopeRow("vs-b", "Except req-b for vendor-b", "vendor-b", null, "req-b", null, "Out", "Owned elsewhere."),
            ],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var scopes = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");
        Assert.Equal(["vs-a"], scopes.EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray());

        var raw = await client.GetStringAsync("/api/v1/freeboard/scopes");
        Assert.DoesNotContain("Owned elsewhere.", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatementOfApplicabilityResolvesInheritanceOrderedById()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/statement-of-applicability/std-a");

        var nodes = json.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());

        // org-a is explicitly In; org-eng (its child, unstated) inherits In. Ordered by id.
        Assert.Equal("org-a", nodes[0].GetProperty("id").GetString());
        Assert.Equal("In", nodes[0].GetProperty("disposition").GetString());
        Assert.Equal("asset", nodes[0].GetProperty("resolution").GetString());

        Assert.Equal("org-eng", nodes[1].GetProperty("id").GetString());
        Assert.Equal("In", nodes[1].GetProperty("disposition").GetString());
        Assert.Equal("inherited", nodes[1].GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task StatementOfApplicabilityIncludesReadableMachineNodesAndExcludesUnreadableOnes()
    {
        // The JSON endpoint's node set is the whole readable forest, not just organisations: a machine
        // under an accessible organisation is a node, one under an inaccessible organisation is not,
        // and a retired discovered machine anchors no read in either subtree.
        var store = PopulatedStore();
        store.Assets =
        [
            TestAssets.Org("org-a"), TestAssets.Org("org-eng", "org-a", "Department"),
            TestAssets.Org("org-x"),
            TestAssets.Machine("m-mine", "org-eng"),
            TestAssets.Machine("m-retired", "org-eng", source: "discovered", state: "Retired"),
            TestAssets.Machine("m-theirs", "org-x"),
        ];
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/statement-of-applicability/std-a");
        var nodes = json.GetProperty("nodes").EnumerateArray().ToList();

        Assert.Equal(["m-mine", "org-a", "org-eng"], nodes.Select(n => n.GetProperty("id").GetString()!).ToArray());
        var machine = nodes.Single(n => n.GetProperty("id").GetString() == "m-mine");
        Assert.Equal("Machine", machine.GetProperty("kind").GetString());
        Assert.Equal("In", machine.GetProperty("disposition").GetString());
        Assert.Equal("inherited", machine.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task StatementOfApplicabilityProjectsPerRequirementDeviations()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/statement-of-applicability/std-a");

        var nodes = json.GetProperty("nodes");
        // org-a is In and excludes req-a and req-b; org-eng inherits both. Ordered by requirement id.
        var orgA = nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "org-a");
        var requirements = orgA.GetProperty("requirements").EnumerateArray().ToList();
        Assert.Equal(2, requirements.Count);
        Assert.Equal("req-a", requirements[0].GetProperty("requirement").GetString());
        Assert.Equal("Out", requirements[0].GetProperty("disposition").GetString());
        Assert.Equal("asset", requirements[0].GetProperty("resolution").GetString());
        Assert.Equal("req-b", requirements[1].GetProperty("requirement").GetString());

        var orgEng = nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "org-eng");
        var inherited = orgEng.GetProperty("requirements").EnumerateArray().ToList();
        Assert.Equal(2, inherited.Count);
        Assert.All(inherited, r => Assert.Equal("inherited", r.GetProperty("resolution").GetString()));
    }

    [Fact]
    public async Task StatementOfApplicabilityDefaultsInWithNoScope()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        // std-b has no Scope rows, so every node defaults In marked "default".
        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/statement-of-applicability/std-b");

        var nodes = json.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        Assert.All(nodes.EnumerateArray(), n =>
        {
            Assert.Equal("In", n.GetProperty("disposition").GetString());
            Assert.Equal("default", n.GetProperty("resolution").GetString());
        });
    }

    [Fact]
    public async Task StatementOfApplicabilityUnknownStandardIsNotFound()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        // An unknown standard must not default every org In; it is absent, so 404 rather than a
        // projection presenting a typo or deleted standard as applicable to all orgs.
        var response = await client.GetAsync("/api/v1/freeboard/statement-of-applicability/std-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StatementOfApplicabilityServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/statement-of-applicability/std-a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StatementOfApplicabilityUnreachableStoreReturns503()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/statement-of-applicability/std-a");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task StatusEndpointReturnsPersistedCounts()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/compliance/status");

        var persisted = json.GetProperty("persisted");
        Assert.Equal(2, persisted.GetProperty("standards").GetInt32());
        Assert.Equal(1, persisted.GetProperty("controls").GetInt32());
        Assert.Equal(2, persisted.GetProperty("requirements").GetInt32());
        Assert.Equal(2, persisted.GetProperty("organisations").GetInt32());
        Assert.Equal(5, persisted.GetProperty("scopes").GetInt32());
        Assert.Equal(2, persisted.GetProperty("vendors").GetInt32());
        Assert.Equal(4, persisted.GetProperty("collectors").GetInt32());

        // The merged persisted shape carries one scopes count and one collectors count: no separate
        // requirementScopes/vendorScopes keys, and no separate evidenceCollectors/attestationTemplates
        // keys. The shape is a hand-written anonymous object, so re-adding a key is a live regression.
        Assert.False(persisted.TryGetProperty("requirementScopes", out _));
        Assert.False(persisted.TryGetProperty("vendorScopes", out _));
        Assert.False(persisted.TryGetProperty("evidenceCollectors", out _));
        Assert.False(persisted.TryGetProperty("attestationTemplates", out _));
    }

    [Fact]
    public async Task ReadEndpointServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/standards");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RequirementsReadServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/requirements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnreachableStoreReturns503ProblemForReads()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        foreach (var path in new[]
                 {
                     "/api/v1/freeboard/standards",
                     "/api/v1/freeboard/requirements",
                     "/api/v1/freeboard/controls",
                     "/api/v1/freeboard/organisations",
                     "/api/v1/freeboard/scopes",
                     "/api/v1/freeboard/vendors",
                     "/api/v1/freeboard/collectors",
                 })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            // Contract-stable RFC 7807 problem title and detail; assert verbatim.
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Compliance store unreachable", problem.GetProperty("title").GetString());
            Assert.Equal(
                "The compliance store could not be reached. Check the database connection.",
                problem.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task UnreachableStoreStatusReturns200WithNullCounts()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/compliance/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var persisted = json.GetProperty("persisted");
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("standards").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("controls").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("requirements").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("organisations").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("scopes").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("vendors").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("collectors").ValueKind);

        // The degraded shape drops the same four retired keys as the populated one.
        Assert.False(persisted.TryGetProperty("requirementScopes", out _));
        Assert.False(persisted.TryGetProperty("vendorScopes", out _));
        Assert.False(persisted.TryGetProperty("evidenceCollectors", out _));
        Assert.False(persisted.TryGetProperty("attestationTemplates", out _));
    }

    [Fact]
    public async Task AnonymousReadIsUnauthorized()
    {
        using var factory = Factory(PopulatedStore());
        using var client = factory.CreateClient();

        foreach (var path in ResourceReadPaths.Append("/api/v1/freeboard/compliance/status"))
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task NonAdminAuthenticatedUserCanReadStatus()
    {
        // Reads require authentication only, not the admin role.
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/compliance/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GitOpsStatusUnchangedAndIndependentOfStore()
    {
        // /api/gitops/status stays anonymous and store-independent even with an unreachable store.
        using var factory = Factory(new FakeComplianceStore { Unreachable = true }, readOnly: true);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/gitops/status");

        Assert.True(json.GetProperty("gitOps").GetBoolean());
        Assert.False(json.TryGetProperty("persisted", out _));
    }
}
