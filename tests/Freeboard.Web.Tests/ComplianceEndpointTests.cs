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
        "/api/v1/freeboard/requirement-scopes",
        "/api/v1/freeboard/vendors",
        "/api/v1/freeboard/vendor-scopes",
        "/api/v1/freeboard/evidence-collectors",
        "/api/v1/freeboard/attestation-templates",
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
        Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
        ],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")],
        RequirementScopes =
        [
            new RequirementScopeRow("rs-a", "Exclude req-a", "org-a", "req-a", "Out"),
            new RequirementScopeRow("rs-b", "Exclude req-b", "org-a", "req-b", "Out"),
        ],
        Vendors =
        [
            new VendorRow("vendor-a", "Vendor A"),
            new VendorRow("vendor-b", "Vendor B"),
        ],
        VendorScopes =
        [
            new VendorScopeRow("vs-a", "Except req-a for vendor-a", "vendor-a", "req-a", null, "Out", "Supports MFA but not SSO."),
            new VendorScopeRow("vs-b", "Include ctrl-a for vendor-a", "vendor-a", null, "ctrl-a", "In", null),
        ],
        Collectors =
        [
            new EvidenceCollectorRow("collector-a", "Endpoint MFA", "ctrl-a", "vendor-a", "integration", "daily", 100,
                new Dictionary<string, string> { ["endpoint"] = "policies.mfa" }),
            new EvidenceCollectorRow("collector-b", "Annual attestation", "ctrl-a", null, "manual-attestation", "annual", null,
                new Dictionary<string, string>()),
        ],
        Templates =
        [
            new AttestationTemplateRow("attest-manual", "Firewall attestation", "ctrl-a", "manual", "Confirm review.",
                [new AttestationField { Id = "reviewed", Label = "Ruleset reviewed?", Type = "boolean" }], null, []),
            new AttestationTemplateRow("attest-training", "Phishing awareness", "ctrl-a", "training", null,
                [], 80, [new QuizItemView("q1", "What should you do?", ["Open it", "Report it"])]),
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
    public async Task EvidenceCollectorsEndpointReturnsRowsWithConfig()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/evidence-collectors");

        Assert.Equal(2, json.GetArrayLength());

        var first = json[0];
        Assert.Equal("collector-a", first.GetProperty("id").GetString());
        Assert.Equal("ctrl-a", first.GetProperty("control").GetString());
        Assert.Equal("vendor-a", first.GetProperty("vendor").GetString());
        Assert.Equal("integration", first.GetProperty("type").GetString());
        Assert.Equal("daily", first.GetProperty("frequency").GetString());
        Assert.Equal(100, first.GetProperty("threshold").GetInt32());
        Assert.Equal("policies.mfa", first.GetProperty("config").GetProperty("endpoint").GetString());

        // Optional vendor/threshold null when absent; empty config serializes as an empty object.
        var second = json[1];
        Assert.Equal(JsonValueKind.Null, second.GetProperty("vendor").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("threshold").ValueKind);
        Assert.Equal(JsonValueKind.Object, second.GetProperty("config").ValueKind);
        Assert.Empty(second.GetProperty("config").EnumerateObject());
    }

    [Fact]
    public async Task EvidenceCollectorsReadServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/freeboard/evidence-collectors")).StatusCode);
    }

    [Fact]
    public async Task EvidenceCollectorsEndpointReturns503WhenStoreUnreachable()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/evidence-collectors");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ZeroGrantEnforceCallerStillReadsEveryCollector()
    {
        // The evidence-collectors endpoint does NOT narrow by IOrgAccess. Under strict Enforce with no
        // grants a member still reads every collector.
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/evidence-collectors");
        Assert.Equal(["collector-a", "collector-b"], json.EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public async Task AttestationTemplatesEndpointReturnsRowsWithFieldsAndQuiz()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/attestation-templates");

        Assert.Equal(2, json.GetArrayLength());

        var manual = json[0];
        Assert.Equal("attest-manual", manual.GetProperty("id").GetString());
        Assert.Equal("ctrl-a", manual.GetProperty("control").GetString());
        Assert.Equal("manual", manual.GetProperty("type").GetString());
        Assert.Equal("Confirm review.", manual.GetProperty("body").GetString());
        var field = manual.GetProperty("fields")[0];
        Assert.Equal("reviewed", field.GetProperty("id").GetString());
        Assert.Equal("boolean", field.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, manual.GetProperty("pass_mark").ValueKind);

        var training = json[1];
        Assert.Equal("training", training.GetProperty("type").GetString());
        Assert.Equal(80, training.GetProperty("pass_mark").GetInt32());
        var item = training.GetProperty("quiz")[0];
        Assert.Equal("q1", item.GetProperty("id").GetString());
        Assert.Equal("What should you do?", item.GetProperty("prompt").GetString());
        Assert.Equal(["Open it", "Report it"], item.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToArray());
    }

    [Fact]
    public async Task AttestationTemplatesEndpointNeverExposesQuizAnswer()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var raw = await client.GetStringAsync("/api/v1/freeboard/attestation-templates");
        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        var item = json[1].GetProperty("quiz")[0];

        Assert.False(item.TryGetProperty("answer", out _));
        // The correct answer is "Report it"; it must not appear anywhere in the JSON.
        Assert.DoesNotContain("answer", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttestationTemplatesReadServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/freeboard/attestation-templates")).StatusCode);
    }

    [Fact]
    public async Task AttestationTemplatesEndpointReturns503WhenStoreUnreachable()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/attestation-templates");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ZeroGrantEnforceCallerStillReadsEveryTemplate()
    {
        // The attestation-templates endpoint does NOT narrow by IOrgAccess. Under strict Enforce with no
        // grants a member still reads every template.
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/attestation-templates");
        Assert.Equal(["attest-manual", "attest-training"], json.EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToArray());
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
    public async Task ScopesEndpointReturnsMapping()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");

        var scope = json[0];
        Assert.Equal("scope-a", scope.GetProperty("id").GetString());
        Assert.Equal("org-a", scope.GetProperty("organisation").GetString());
        Assert.Equal("std-a", scope.GetProperty("standard").GetString());
        Assert.Equal("In", scope.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task RequirementScopesEndpointReturnsMappingOrderedById()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/requirement-scopes");

        Assert.Equal(2, json.GetArrayLength());
        // Ordered by id: rs-a before rs-b.
        Assert.Equal("rs-a", json[0].GetProperty("id").GetString());
        Assert.Equal("org-a", json[0].GetProperty("organisation").GetString());
        Assert.Equal("req-a", json[0].GetProperty("requirement").GetString());
        Assert.Equal("Out", json[0].GetProperty("disposition").GetString());
        Assert.Equal("rs-b", json[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task RequirementScopesReadServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/requirement-scopes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
    public async Task VendorScopesEndpointReturnsTargetsDispositionsAndJustifications()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/vendor-scopes");

        Assert.Equal(2, json.GetArrayLength());

        // vs-a: requirement target, Out, justification present. control null.
        var vsA = json[0];
        Assert.Equal("vs-a", vsA.GetProperty("id").GetString());
        Assert.Equal("vendor-a", vsA.GetProperty("vendor").GetString());
        Assert.Equal("req-a", vsA.GetProperty("requirement").GetString());
        Assert.Equal(JsonValueKind.Null, vsA.GetProperty("control").ValueKind);
        Assert.Equal("Out", vsA.GetProperty("disposition").GetString());
        Assert.Equal("Supports MFA but not SSO.", vsA.GetProperty("justification").GetString());

        // vs-b: control target, In, no justification. requirement null.
        var vsB = json[1];
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
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/freeboard/vendor-scopes")).StatusCode);
    }

    [Fact]
    public async Task ZeroGrantEnforceCallerStillReadsEveryVendor()
    {
        // Unlike /organisations, the vendor endpoints do NOT narrow by IOrgAccess. Under strict
        // Enforce with no grants, AuthzOrgAccess yields an empty accessible-org set, yet a member
        // still reads every vendor and every vendor-scope (including Out justifications).
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var vendors = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/vendors");
        Assert.Equal(["vendor-a", "vendor-b"], vendors.EnumerateArray().Select(v => v.GetProperty("id").GetString()!).ToArray());

        var scopes = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/vendor-scopes");
        Assert.Equal(2, scopes.GetArrayLength());
        Assert.Equal("Supports MFA but not SSO.", scopes[0].GetProperty("justification").GetString());
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
        Assert.Equal("explicit", nodes[0].GetProperty("resolution").GetString());

        Assert.Equal("org-eng", nodes[1].GetProperty("id").GetString());
        Assert.Equal("In", nodes[1].GetProperty("disposition").GetString());
        Assert.Equal("inherited", nodes[1].GetProperty("resolution").GetString());
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
        Assert.Equal("explicit", requirements[0].GetProperty("resolution").GetString());
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
        Assert.Equal(1, persisted.GetProperty("scopes").GetInt32());
        Assert.Equal(2, persisted.GetProperty("requirementScopes").GetInt32());
        Assert.Equal(2, persisted.GetProperty("vendors").GetInt32());
        Assert.Equal(2, persisted.GetProperty("vendorScopes").GetInt32());
        Assert.Equal(2, persisted.GetProperty("evidenceCollectors").GetInt32());
        Assert.Equal(2, persisted.GetProperty("attestationTemplates").GetInt32());
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
                     "/api/v1/freeboard/requirement-scopes",
                     "/api/v1/freeboard/vendors",
                     "/api/v1/freeboard/vendor-scopes",
                     "/api/v1/freeboard/evidence-collectors",
                     "/api/v1/freeboard/attestation-templates",
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
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("requirementScopes").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("vendors").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("vendorScopes").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("evidenceCollectors").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("attestationTemplates").ValueKind);
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
