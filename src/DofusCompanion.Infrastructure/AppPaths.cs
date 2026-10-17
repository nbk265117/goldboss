namespace DofusCompanion.Infrastructure;

/// <summary>
/// Local data locations. Everything lives under %LOCALAPPDATA%\DofusCompanion unless the user moves the
/// database. No administrator rights are needed.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DofusCompanion");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }
    public string Settings => Path.Combine(Root, "settings.json");
    public string DefaultDatabase => Path.Combine(Root, "knowledge.db");
    public string Logs => Path.Combine(Root, "logs");
    public string Screenshots => Path.Combine(Root, "screenshots");
    public string Exports => Path.Combine(Root, "exports");
    /// <summary>Knowledge packages dropped here (e.g. by the DofusDB importer) are applied at startup.</summary>
    public string Imports => Path.Combine(Root, "imports");

    /// <summary>Resolves a user-chosen database path; rejects anything that is not a plain .db file path.</summary>
    public string ResolveDatabase(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return DefaultDatabase;
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        if (!string.Equals(Path.GetExtension(full), ".db", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The database file must have the .db extension.", nameof(configured));
        return full;
    }
}
