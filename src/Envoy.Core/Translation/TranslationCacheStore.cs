using System.Globalization;
using System.Text;
using System.Text.Json;
using Envoy.Core.Diagnostics;

namespace Envoy.Core.Translation;

/// <summary>
/// <c>translation-cache.json</c> (REQ-TRR-20, 21): the opt-in saved translations. Holds the translated text, the
/// detected language, the provider and a SHA-256 of each original line, never the line itself. Saves are atomic (temp
/// file, then replace) and capped in bytes; a corrupt file is renamed to <c>.bad</c> and Envoy starts empty.
/// </summary>
public sealed class TranslationCacheStore(string path, ILog? log = null)
{
    public const string FileName = "translation-cache.json";
    public const int FormatVersion = 1;
    public const int DefaultMaxBytes = 1 << 20;

    private readonly ILog _log = log ?? NullLog.Instance;

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));
    public string TempPath => Path + ".tmp";
    public string BadPath => Path + ".bad";

    /// <summary>Tests only: runs just before the temp file is written.</summary>
    internal Action? BeforeWrite { get; set; }

    /// <summary>The saved entries, most recent first; [] when there is no file. A corrupt file is renamed to <c>.bad</c>.</summary>
    public IReadOnlyList<CacheEntry> Load()
    {
        if (!File.Exists(Path)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path));
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != FormatVersion) throw new FormatException("Unknown cache format version.");
            var entries = new List<CacheEntry>();
            foreach (var e in root.GetProperty("entries").EnumerateArray())
            {
                entries.Add(new CacheEntry(
                    e.GetProperty("k").GetString() ?? throw new FormatException("Missing key."),
                    e.GetProperty("t").GetString() ?? "",
                    e.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                    e.GetProperty("p").GetString() ?? "",
                    DateTimeOffset.Parse(e.GetProperty("at").GetString() ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
            }
            return entries;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            _log.Warn("Saved translations could not be read; starting with none", null, LogField.Of("error", ex.GetType().Name));
            try
            {
                File.Move(Path, BadPath, overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                _log.Warn("Could not set the damaged saved translations aside", null, LogField.Of("error", moveError.GetType().Name));
            }
            return [];
        }
    }

    /// <summary>
    /// Writes <paramref name="entries"/> (most recent first) atomically, dropping the least recently used ones that do
    /// not fit in <paramref name="maxBytes"/>. False (logged at Warning) when the disk refused.
    /// </summary>
    public bool Save(IReadOnlyList<CacheEntry> entries, int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var bytes = Serialize(entries, maxBytes);
        try
        {
            var folder = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            BeforeWrite?.Invoke();
            File.WriteAllBytes(TempPath, bytes);
            File.Move(TempPath, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Saving translations failed; will retry", null, LogField.Of("error", ex.GetType().Name));
            TryDelete(TempPath);
            return false;
        }
    }

    /// <summary>Deletes the file and its <c>.tmp</c>. False (logged) when the file exists but could not be deleted.</summary>
    public bool Delete()
    {
        TryDelete(TempPath);
        return TryDelete(Path);
    }

    /// <summary>The file bytes: <c>{"version":1,"entries":[{"k","t","d","p","at"}]}</c>, at most <paramref name="maxBytes"/>.</summary>
    internal static byte[] Serialize(IReadOnlyList<CacheEntry> entries, int maxBytes)
    {
        var header = Encoding.UTF8.GetBytes("{\"version\":" + FormatVersion.ToString(CultureInfo.InvariantCulture) + ",\"entries\":[");
        var footer = "]}"u8.ToArray();
        using var body = new MemoryStream();
        var budget = maxBytes - header.Length - footer.Length;
        var first = true;
        foreach (var entry in entries)
        {
            var item = SerializeEntry(entry);
            var cost = item.Length + (first ? 0 : 1);
            if (body.Length + cost > budget) break;
            if (!first) body.WriteByte((byte)',');
            body.Write(item);
            first = false;
        }
        using var file = new MemoryStream(header.Length + (int)body.Length + footer.Length);
        file.Write(header);
        body.WriteTo(file);
        file.Write(footer);
        return file.ToArray();
    }

    private static byte[] SerializeEntry(CacheEntry e)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("k", e.KeyHash);
            w.WriteString("t", e.Text);
            if (e.DetectedSourceLanguage is null) w.WriteNull("d");
            else w.WriteString("d", e.DetectedSourceLanguage);
            w.WriteString("p", e.ProviderId);
            w.WriteString("at", e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private bool TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Deleting saved translations failed", null, LogField.Of("error", ex.GetType().Name));
            return false;
        }
    }
}
