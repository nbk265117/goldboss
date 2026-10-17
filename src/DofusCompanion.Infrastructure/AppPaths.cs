namespace DofusCompanion.Infrastructure;

/// <summary>
/// Local data locations. Everything lives under %LOCALAPPDATA%\DofusGoldBoss unless the user moves the
/// database. No administrator rights are needed.
/// </summary>
public sealed class AppPaths
{
    public const string FolderName = "DofusGoldBoss";
    /// <summary>Folder used before the app was renamed (versions up to 0.3.x).</summary>
    public const string LegacyFolderName = "DofusCompanion";

    public AppPaths(string? root = null)
    {
        Root = root ?? DefaultRoot();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }

    /// <summary>
    /// %LOCALAPPDATA%\DofusGoldBoss. On first start after the rename, the previous DofusCompanion folder is moved
    /// there so no data is lost; if it cannot be moved (e.g. an old version is still running) it keeps being used.
    /// </summary>
    public static string DefaultRoot(string? localAppData = null)
    {
        var baseDir = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(baseDir, FolderName);
        var legacy = Path.Combine(baseDir, LegacyFolderName);
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
        try
        {
            Directory.Move(legacy, current);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
    }
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
