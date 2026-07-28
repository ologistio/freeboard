using System.Text.Json;
using System.Text.Json.Serialization;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence;

/// <summary>
/// The shape of the <c>collectors.config</c> column. It is a PROJECTION of the authored
/// <see cref="CollectorConfig"/>, not that record: an absent member is omitted rather than written as a
/// JSON null or an empty array, and <see cref="PassMark"/> is stored as a JSON number rather than as the
/// raw authored text. The import plan writes the column through <see cref="Write"/> and the read store
/// reads it through <see cref="Read"/>, both over the one <see cref="Options"/> instance, so the two
/// cannot drift.
///
/// Each stored quiz item RETAINS its answer for the later grading runtime; redaction happens on the way
/// out, in <see cref="Read"/>, never in the column.
/// </summary>
internal sealed record StoredCollectorConfig
{
    /// <summary>
    /// Default naming, so the stored keys are the C# member names - which is what the pre-merge
    /// <c>checks</c>, <c>fields</c>, and <c>quiz</c> columns already hold per item, and what migration
    /// <c>021</c> composes in SQL - plus null-omitting, which is what drops an absent member. These are
    /// NOT the serializer defaults.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string? Body { get; init; }

    public List<AttestationField>? Fields { get; init; }

    public int? PassMark { get; init; }

    public List<QuizItem>? Quiz { get; init; }

    public List<Check>? Checks { get; init; }

    /// <summary>
    /// Serializes <paramref name="config"/> for storage, or returns null when no member is present - an
    /// empty config is SQL NULL, not <c>{}</c>. An empty list and a blank scalar both map to null first,
    /// so the ignore condition drops them.
    /// </summary>
    internal static string? Write(CollectorConfig config, int? passMark)
    {
        var stored = new StoredCollectorConfig
        {
            Body = string.IsNullOrWhiteSpace(config.Body) ? null : config.Body,
            Fields = config.Fields.Count == 0 ? null : config.Fields,
            PassMark = passMark,
            Quiz = config.Quiz.Count == 0 ? null : config.Quiz,
            Checks = config.Checks.Count == 0 ? null : config.Checks,
        };

        if (stored is { Body: null, Fields: null, PassMark: null, Quiz: null, Checks: null })
        {
            return null;
        }

        return JsonSerializer.Serialize(stored, Options);
    }

    /// <summary>
    /// Binds the stored column and projects it to the read model, dropping each quiz item's answer. A NULL
    /// or empty column reads as the empty view, which is the same view a collector whose schema registers
    /// no key produces.
    /// </summary>
    internal static CollectorConfigView Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return CollectorConfigView.Empty;
        }

        var stored = JsonSerializer.Deserialize<StoredCollectorConfig>(json, Options);
        if (stored is null)
        {
            return CollectorConfigView.Empty;
        }

        return new CollectorConfigView(
            stored.Body,
            stored.Fields ?? [],
            stored.PassMark,
            stored.Quiz?.Select(q => new QuizItemView(q.Id, q.Prompt, q.Options)).ToList() ?? [],
            stored.Checks ?? []);
    }
}
