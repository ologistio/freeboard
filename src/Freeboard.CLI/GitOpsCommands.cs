using ConsoleAppFramework;
using Freeboard.Core.GitOps;
using Freeboard.Persistence.System;

namespace Freeboard.CLI;

/// <summary>
/// The <c>gitops</c> command group. Loads and validates declarative compliance config
/// from a directory. <c>validate</c> and <c>apply --dry-run</c> make no network calls
/// and write no state. <c>sync</c> connects to the database and writes the persisted
/// compliance set.
///
/// Exit codes follow the CLI convention (0 success, 1 input/validation, 3 operational),
/// with one group-specific addition: <c>apply</c> without <c>--dry-run</c> returns 2 to
/// signal the not-yet-implemented real apply, distinct from a config-validation failure (1).
/// </summary>
public sealed class GitOpsCommands
{
    /// <summary>Validate a GitOps config directory.</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    public int Validate([Argument] string path)
    {
        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        PrintWarnings(result);
        PrintSummary(result.Config);
        return 0;
    }

    /// <summary>Show the config state that would be applied (dry-run only).</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    /// <param name="dryRun">Required in this version. Real apply lands in a later increment.</param>
    public int Apply([Argument] string path, bool dryRun = false)
    {
        if (!dryRun)
        {
            Console.Error.WriteLine(
                "Only 'apply --dry-run' is supported in this version. Real apply lands in a later increment.");
            return 2;
        }

        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        PrintWarnings(result);
        Console.WriteLine("Planned config state (dry-run, nothing written):");
        PrintPlannedState(result.Config);
        return 0;
    }

    /// <summary>Load, validate, and import a GitOps config into the persisted store.</summary>
    /// <param name="path">Directory containing the YAML config.</param>
    /// <param name="connectionString">-c, MySQL connection string. Overrides FREEBOARD_DB.</param>
    /// <param name="migrate">Apply pending schema migrations before importing.</param>
    public int Sync([Argument] string path, string? connectionString = null, bool migrate = false)
    {
        var result = ConfigValidator.LoadAndValidate(path);
        if (!result.IsValid)
        {
            PrintDiagnostics(result);
            return 1;
        }

        // Warnings do not block sync (dangling/missing edges, parent cycles), but the operator must see
        // them, so print them on the valid path before touching the database.
        PrintWarnings(result);

        var resolved = ConnectionStringResolver.Resolve(connectionString);
        if (resolved is null)
        {
            Console.Error.WriteLine(
                $"No connection string. Pass --connection-string or set {ConnectionStringResolver.EnvVar}.");
            return 3;
        }

        try
        {
            var runner = PersistenceFactory.CreateMigrationRunner(resolved);

            // Migrate-first gate: GetState is read-only and creates nothing.
            var state = runner.GetStateAsync().GetAwaiter().GetResult();

            // An integrity-violated schema must never be imported into, with or without
            // --migrate. GetState reports this by a pure read; fail before importing.
            if (state.IsCorrupt)
            {
                Console.Error.WriteLine($"{state.IntegrityError} Nothing was written.");
                return 3;
            }

            if (!state.IsCurrent)
            {
                if (!migrate)
                {
                    Console.Error.WriteLine(
                        "Schema out of date; run 'system migrate' or pass --migrate. Nothing was written.");
                    return 3;
                }

                runner.ApplyPendingAsync().GetAwaiter().GetResult();
            }

            var importer = PersistenceFactory.CreateImporter(resolved);
            importer.ImportAsync(result.Config).GetAwaiter().GetResult();
        }
        catch (MigrationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex) when (ex is global::System.Data.Common.DbException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Database operation failed: {ex.Message}");
            return 3;
        }

        Console.WriteLine(
            $"Synced: {result.Config.Standards.Count} standard(s), {result.Config.Requirements.Count} requirement(s), "
            + $"{result.Config.Controls.Count} control(s), {AssetSummary(result.Config)}, "
            + $"{result.Config.Scopes.Count} scope(s), {result.Config.RequirementScopes.Count} requirement-scope(s), "
            + $"{result.Config.VendorScopes.Count} vendor-scope(s), "
            + $"{result.Config.EvidenceCollectors.Count} evidence-collector(s), "
            + $"{result.Config.AttestationTemplates.Count} attestation-template(s).");
        return 0;
    }

    private static void PrintDiagnostics(ConfigResult result)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            Console.Error.WriteLine(diagnostic.ToString());
        }
    }

    private static void PrintWarnings(ConfigResult result)
    {
        foreach (var warning in result.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }

    /// <summary>A one-line asset breakdown by type for the summary lines.</summary>
    private static string AssetSummary(GitOpsConfig config)
    {
        var byType = config.Assets.GroupBy(a => a.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int Count(string type) => byType.TryGetValue(type, out var n) ? n : 0;
        return $"{config.Assets.Count} asset(s) ({Count("Company")} company, {Count("Department")} department, "
            + $"{Count("Machine")} machine, {Count("Vendor")} vendor)";
    }

    private static void PrintSummary(GitOpsConfig config)
    {
        Console.WriteLine(
            $"OK: {config.Standards.Count} standard(s), {config.Requirements.Count} requirement(s), "
            + $"{config.Controls.Count} control(s), {AssetSummary(config)}, "
            + $"{config.Scopes.Count} scope(s), {config.RequirementScopes.Count} requirement-scope(s), "
            + $"{config.VendorScopes.Count} vendor-scope(s), "
            + $"{config.EvidenceCollectors.Count} evidence-collector(s), "
            + $"{config.AttestationTemplates.Count} attestation-template(s).");
    }

    private static void PrintPlannedState(GitOpsConfig config)
    {
        Console.WriteLine($"Standards ({config.Standards.Count}):");
        foreach (var standard in config.Standards)
        {
            Console.WriteLine($"  - {standard.Id}: {standard.Title}");
        }

        Console.WriteLine($"Requirements ({config.Requirements.Count}):");
        foreach (var requirement in config.Requirements)
        {
            Console.WriteLine($"  - {requirement.Id}: {requirement.Title} [{requirement.Theme}] -> {requirement.Standard}");
        }

        Console.WriteLine($"Controls ({config.Controls.Count}):");
        foreach (var control in config.Controls)
        {
            Console.WriteLine($"  - {control.Id}: {control.Title} -> [{string.Join(", ", control.MapsTo)}]");
        }

        Console.WriteLine($"Assets ({config.Assets.Count}):");
        foreach (var asset in config.Assets)
        {
            var edge = !string.IsNullOrEmpty(asset.Owner)
                ? $"owner={asset.Owner}"
                : $"parent={(string.IsNullOrEmpty(asset.Parent) ? "(root)" : asset.Parent)}";
            Console.WriteLine($"  - {asset.Id}: {asset.Title} [{asset.Type}] {edge}");
        }

        Console.WriteLine($"Scopes ({config.Scopes.Count}):");
        foreach (var scope in config.Scopes)
        {
            Console.WriteLine(
                $"  - {scope.Id}: {scope.Title} -> {scope.Organisation} / {scope.Standard} = {scope.Disposition}");
        }

        Console.WriteLine($"RequirementScopes ({config.RequirementScopes.Count}):");
        foreach (var requirementScope in config.RequirementScopes)
        {
            Console.WriteLine(
                $"  - {requirementScope.Id}: {requirementScope.Title} -> {requirementScope.Organisation} / "
                + $"{requirementScope.Requirement} = {requirementScope.Disposition}");
        }

        Console.WriteLine($"VendorScopes ({config.VendorScopes.Count}):");
        foreach (var vendorScope in config.VendorScopes)
        {
            var target = string.IsNullOrEmpty(vendorScope.Requirement)
                ? $"control {vendorScope.Control}"
                : $"requirement {vendorScope.Requirement}";
            Console.WriteLine(
                $"  - {vendorScope.Id}: {vendorScope.Title} -> {vendorScope.Vendor} / {target} = {vendorScope.Disposition}");
        }

        Console.WriteLine($"EvidenceCollectors ({config.EvidenceCollectors.Count}):");
        foreach (var collector in config.EvidenceCollectors)
        {
            var vendor = string.IsNullOrEmpty(collector.Vendor) ? "-" : collector.Vendor;
            Console.WriteLine(
                $"  - {collector.Id}: {collector.Title} -> control {collector.Control} / vendor {vendor} "
                + $"[{collector.Type}, {collector.Frequency}]");
        }

        // Per-template line only (id/title/control/type, plus pass_mark for training); body, fields, quiz,
        // and the confidential quiz answer are deliberately omitted from authoring output.
        Console.WriteLine($"AttestationTemplates ({config.AttestationTemplates.Count}):");
        foreach (var template in config.AttestationTemplates)
        {
            var passMark = template.Type == "training" && !string.IsNullOrWhiteSpace(template.PassMark)
                ? $", pass_mark {template.PassMark}"
                : string.Empty;
            Console.WriteLine(
                $"  - {template.Id}: {template.Title} -> control {template.Control} [{template.Type}{passMark}]");
        }
    }
}
