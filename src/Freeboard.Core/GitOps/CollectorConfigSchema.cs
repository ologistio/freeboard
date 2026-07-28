namespace Freeboard.Core.GitOps;

/// <summary>
/// One <c>config</c> key a <c>(type, provider)</c> pair accepts. <see cref="Required"/> is evaluated
/// against the parsed value, never against the key's presence in the document (see
/// <see cref="CollectorConfigSchema.HasValue"/>).
/// </summary>
public sealed record CollectorConfigKey(string Name, bool Required);

/// <summary>
/// The single owner of a <see cref="Collector"/>'s <c>config</c> key set: which keys each
/// <c>(type, provider)</c> pair accepts and which of them it requires. No rule this table expresses is
/// also hard-coded elsewhere - the loader diffs an authored <c>config</c> mapping's keys against
/// <see cref="For"/>, and the validator enforces <see cref="CollectorConfigKey.Required"/> against
/// <see cref="HasValue"/>; neither restates a key name of its own.
///
/// Keying on <c>(type, provider)</c> rather than on <c>type</c> alone is what lets a second integration
/// provider register a different key set instead of inheriting the union of every provider's keys.
/// </summary>
public static class CollectorConfigSchema
{
    public const string KeyBody = "body";
    public const string KeyFields = "fields";
    public const string KeyPassMark = "pass_mark";
    public const string KeyQuiz = "quiz";
    public const string KeyChecks = "checks";

    /// <summary>The provider component for a type that carries no provider.</summary>
    private const string NoProvider = "";

    // `body` and `fields` are registered on BOTH attestation types and are optional on both; only
    // `pass_mark` and `quiz` are type-conditional. That is the pre-merge attestation rule expressed as a
    // key set: every form field was optional on the record, the form-field rules ran for whichever type
    // declared `fields`, and only the pass mark and the quiz were required on training and rejected on
    // manual. Making `fields` required on manual, or omitting it from training, would change what
    // validates.
    //
    // `checks` is registered per provider rather than kept as a top-level collector field because a
    // check's `source_key` is a provider-native id by definition, which makes the check list the most
    // provider-shaped payload in the model - so it is what makes the provider component of the key
    // load-bearing rather than scaffolding.
    //
    // `script` and `agent` register the EMPTY schema deliberately: no script or agent runner exists to
    // consume a key, and an empty schema still rejects every `config` key authored on such a collector.
    private static readonly IReadOnlyDictionary<(string Type, string Provider), IReadOnlyList<CollectorConfigKey>> Schemas =
        new Dictionary<(string, string), IReadOnlyList<CollectorConfigKey>>()
        {
            [("manual", NoProvider)] =
            [
                new CollectorConfigKey(KeyBody, Required: false),
                new CollectorConfigKey(KeyFields, Required: false),
            ],
            [("training", NoProvider)] =
            [
                new CollectorConfigKey(KeyBody, Required: false),
                new CollectorConfigKey(KeyFields, Required: false),
                new CollectorConfigKey(KeyPassMark, Required: true),
                new CollectorConfigKey(KeyQuiz, Required: true),
            ],
            [("integration", "fleet")] =
            [
                new CollectorConfigKey(KeyChecks, Required: true),
            ],
            [("script", NoProvider)] = [],
            [("agent", NoProvider)] = [],
        };

    /// <summary>
    /// The ordered keys registered for <paramref name="type"/> and <paramref name="provider"/>, or null
    /// when no schema is registered - because the type token is unknown, the provider token is unknown, or
    /// an integration collector omits its provider entirely. Null means the caller has already reported
    /// that token and MUST NOT also report an authored key or a required one, so one authoring mistake
    /// yields one diagnostic rather than a cascade. Requiredness is a property of the PAIR, so an
    /// unresolved pair has none to report without hard-coding a single provider's key set.
    ///
    /// A provider a type cannot legally carry is NOT one of those cases: it does not select a key set, so
    /// it must not hide one either.
    /// </summary>
    public static IReadOnlyList<CollectorConfigKey>? For(string? type, string? provider)
    {
        var typeToken = type ?? string.Empty;
        var providerToken = string.IsNullOrWhiteSpace(provider) ? NoProvider : provider;
        if (Schemas.TryGetValue((typeToken, providerToken), out var schema))
        {
            return schema;
        }

        // A provider selects a key set only where the model permits one. Off the integration path a
        // provider is illegal and reported in its own right, and every row such a type registers is a
        // no-provider row - so retrying without it yields the one key set that type can ever have,
        // rather than silently dropping its required-key and unknown-key diagnostics behind a token
        // the author must delete anyway. On the integration path the retry finds nothing, because no
        // (integration, -) row exists: that is what keeps an absent or unknown provider resolving to
        // no schema, where the provider genuinely is the missing input to the lookup.
        return providerToken != NoProvider && Schemas.TryGetValue((typeToken, NoProvider), out var byType)
            ? byType
            : null;
    }

    /// <summary>
    /// True when <paramref name="config"/> carries a value for the registered key
    /// <paramref name="name"/>. Requiredness is a VALUE test, not a key-presence test: an empty list and
    /// a blank scalar both count as absent, which is what keeps <c>quiz: []</c>, <c>pass_mark: ""</c>,
    /// and <c>checks: []</c> failing as they did before the merge. An unregistered name has no member and
    /// so can never be satisfied.
    /// </summary>
    public static bool HasValue(CollectorConfig config, string name) => name switch
    {
        KeyBody => !string.IsNullOrWhiteSpace(config.Body),
        KeyFields => config.Fields.Count > 0,
        KeyPassMark => !string.IsNullOrWhiteSpace(config.PassMark),
        KeyQuiz => config.Quiz.Count > 0,
        KeyChecks => config.Checks.Count > 0,
        _ => false,
    };
}
