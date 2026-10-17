using DofusCompanion.Domain;

namespace DofusCompanion.Infrastructure;

/// <summary>
/// Local entity pictures (boss/monster portraits) under &lt;data&gt;\images: the boss pictures shipped with the
/// release (copied from data\dofusdb at first start), those the DofusDB importer downloads, and those the user
/// adds (file, clipboard). Lookup order: a picture the user attached (&lt;entityId&gt;.png), then the entity's
/// "image" attribute (a path relative to the images folder, e.g. "dofusdb/31.png").
/// </summary>
public sealed class EntityImageStore(AppPaths paths)
{
    public const string ImageAttribute = "image";
    public const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    public string Directory => Path.Combine(paths.Root, "images");

    /// <summary>Full path of the entity's picture, or null when there is none (or it is invalid/unsafe).</summary>
    public string? Resolve(GameEntity entity)
    {
        var own = UserImagePath(entity.Id);
        if (File.Exists(own)) return own;
        return entity.Attributes.TryGetValue(ImageAttribute, out var relative) ? ResolveRelative(relative) : null;
    }

    /// <summary>Resolves a relative picture path, refusing anything that escapes the images folder.</summary>
    public string? ResolveRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var root = Path.GetFullPath(Directory) + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relative)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null; // path traversal
        if (!AllowedExtensions.Contains(Path.GetExtension(full).ToLowerInvariant())) return null;
        var info = new FileInfo(full);
        return info.Exists && info.Length <= MaxFileBytes ? full : null;
    }

    /// <summary>Saves a PNG the user attached to an entity (replaces a previous one).</summary>
    public string SaveUserImage(string entityId, byte[] png)
    {
        if (png.Length == 0 || png.Length > MaxFileBytes) throw new IOException("Image too large or empty.");
        var path = UserImagePath(entityId);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, png);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public bool RemoveUserImage(string entityId)
    {
        var path = UserImagePath(entityId);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public bool HasUserImage(string entityId) => File.Exists(UserImagePath(entityId));

    private static string SafeName(string entityId) =>
        new(entityId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    /// <summary>Entity ids are [a-z0-9._-] (schema-enforced), so they are safe file names.</summary>
    private string UserImagePath(string entityId)
    {
        return Path.Combine(Directory, "user", SafeName(entityId) + ".png");
    }
}
