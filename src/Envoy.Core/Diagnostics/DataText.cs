using System.Globalization;
using Envoy.Core.Text;

namespace Envoy.Core.Diagnostics;

/// <summary>Text for "Delete all Envoy data" and "Reset settings" (contract §8.7).</summary>
public static class DataText
{
    public const string DeleteAllTitle = "Delete all Envoy data?";

    /// <summary>{count} = number of files; {folder} = the data folder; {list} = one file per line.</summary>
    public const string DeleteAllFormat =
        "Envoy will delete {count} files from {folder}, including your settings, API keys, phrases, logs and transcripts, then restart as if newly installed. This can't be undone.\n\n{list}";

    public const string DeleteAllNothing = "Envoy has no data files on this PC yet. It will remove its sign-in start-up entry and restart.";

    public const string SomeFilesKeptTitle = "Some files were kept";

    /// <summary>{list} = one file per line.</summary>
    public const string SomeFilesKeptFormat =
        "Envoy couldn't delete these files because another program is using them. You can delete them yourself from the data folder.\n\n{list}";

    public const string ResetTitle = "Reset all settings?";

    public const string ResetMessage =
        "Envoy will restart with default settings, and you'll need to enter your API keys again. A copy of your current settings is kept in the data folder.";

    /// <summary>The Delete-all confirmation built from the inventory.</summary>
    public static string DeleteAllMessage(IReadOnlyList<DataFile> files, string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return DeleteAllNothing;
        var list = string.Join("\n", files.Select(f => "• " + Path.GetRelativePath(dataFolder, f.Path)));
        return TextTemplate.Format(DeleteAllFormat, CultureInfo.CurrentCulture,
            ("count", files.Count), ("folder", dataFolder), ("list", list));
    }

    /// <summary>The message after Delete all when some files could not be removed.</summary>
    public static string SomeFilesKept(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return TextTemplate.Format(SomeFilesKeptFormat, null, ("list", string.Join("\n", paths.Select(p => "• " + p))));
    }
}
