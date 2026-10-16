using System.Text.RegularExpressions;

namespace Envoy.Core.Diagnostics;

/// <param name="Pattern">Relative to the data folder; <c>*</c> matches any characters except a path separator.</param>
public sealed record DataItem(string Pattern, string Purpose);

public sealed record DataFile(string Path, string Pattern, long Bytes);

public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed);

/// <summary>
/// Every file Envoy stores in its data folder and why (REQ-REL-44, contract §8.2). "Delete all Envoy data" deletes
/// exactly these. A new data file joins <see cref="Items"/> and <c>docs/privacy.md</c> in the same change.
/// </summary>
public static class DataInventory
{
    public const string LogsFolderName = "logs";

    public static IReadOnlyList<DataItem> Items { get; } =
    [
        new("settings.json", "Your settings; API keys are encrypted for your Windows account."),
        new("settings.json.bak", "Last settings that loaded correctly."),
        new("settings.json.v*.bak", "Copy kept before a settings upgrade."),
        new("settings.json.reset-*.bak", "Copy kept when you reset settings."),
        new("settings.json.broken", "A damaged settings file kept for troubleshooting."),
        new("settings.json.tmp", "Left over if Envoy closed while saving."),
        new("phrases.json", "Your own quick phrases."),
        new("logs/envoy*.log", "Diagnostic log."),
        new("logs/chat-*.txt", "Chat transcripts, if you turned them on."),
        new("last-crash.json", "Details of the last crash."),
        new("last-crash.json.tmp", "Left over if Envoy closed while recording a crash."),
        new("session.json", "Start-up marker used to detect crashes."),
        new("translation-cache.json", "Saved translations, if you turned on \"Remember translations\"."),
        new("translation-cache.json.tmp", "Left over if Envoy closed while saving translations."),
        new("translation-cache.json.bad", "A damaged saved-translations file set aside."),
        new("chat-formats.json", "Your own chat format profiles (you create this file)."),
        new("error.log", "Error log from Envoy 0.1."),
    ];

    private static readonly (DataItem Item, Regex Regex, bool InLogs)[] Matchers = Items
        .Select(i => (i, ToRegex(i.Pattern), i.Pattern.StartsWith(LogsFolderName + "/", StringComparison.Ordinal)))
        .ToArray();

    /// <summary>The inventory files that exist in <paramref name="dataFolder"/> (top level and <c>logs/</c> only).</summary>
    public static IReadOnlyList<DataFile> Enumerate(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        var found = new List<DataFile>();
        if (!Directory.Exists(dataFolder)) return found;
        Collect(dataFolder, "", inLogs: false, found);
        var logs = Path.Combine(dataFolder, LogsFolderName);
        if (Directory.Exists(logs) && !IsReparsePoint(logs)) Collect(logs, LogsFolderName + "/", inLogs: true, found);
        return found;
    }

    /// <summary>
    /// Deletes only inventory files. Never recurses beyond <c>logs/</c>, never follows a reparse-point directory, and
    /// removes <c>logs/</c> and the data folder only if they end up empty. Collects failures; never throws.
    /// </summary>
    public static DeleteResult DeleteAll(string dataFolder) => DeleteAll(dataFolder, File.Delete);

    /// <summary><see cref="DeleteAll(string)"/> with the file deletion injected, so tests can simulate a locked file on any OS.</summary>
    internal static DeleteResult DeleteAll(string dataFolder, Action<string> deleteFile)
    {
        var deleted = new List<string>();
        var failed = new List<string>();
        IReadOnlyList<DataFile> files;
        try
        {
            files = Enumerate(dataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DeleteResult(deleted, [dataFolder]);
        }
        foreach (var file in files)
        {
            try
            {
                deleteFile(file.Path);
                deleted.Add(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(file.Path);
            }
        }
        RemoveIfEmpty(Path.Combine(dataFolder, LogsFolderName));
        RemoveIfEmpty(dataFolder);
        return new DeleteResult(deleted, failed);
    }

    /// <summary>The inventory item a relative path (forward slashes) belongs to, or null.</summary>
    public static DataItem? Match(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var normalized = relativePath.Replace('\\', '/');
        return Matchers.FirstOrDefault(m => m.Regex.IsMatch(normalized)).Item;
    }

    private static void Collect(string folder, string prefix, bool inLogs, List<DataFile> found)
    {
        foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = prefix + Path.GetFileName(path);
            var match = Matchers.FirstOrDefault(m => m.InLogs == inLogs && m.Regex.IsMatch(relative));
            if (match.Item is null) continue;
            long bytes;
            try
            {
                bytes = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bytes = 0;
            }
            found.Add(new DataFile(path, match.Item.Pattern, bytes));
        }
    }

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !IsReparsePoint(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty after all, or in use: leave it.
        }
    }

    private static bool IsReparsePoint(string folder) =>
        new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", "[^/\\\\]*", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
