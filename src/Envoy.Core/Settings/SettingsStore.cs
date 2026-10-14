using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Envoy.Core.Diagnostics;

namespace Envoy.Core.Settings;

/// <summary>Encrypts secrets at rest. The Windows app supplies a DPAPI implementation.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

public sealed class NoOpProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedText) => protectedText;
}

/// <summary>How <see cref="SettingsStore.LoadWithReport"/> got its settings (REQ-REL-34).</summary>
public enum SettingsLoadStatus
{
    /// <summary>No settings file: first run, or after a reset.</summary>
    Defaults,

    /// <summary>The settings file loaded.</summary>
    Loaded,

    /// <summary>The settings file was damaged; the last known good copy (<c>.bak</c>) loaded instead.</summary>
    RecoveredFromBackup,

    /// <summary>The settings file and its backup were both unusable; defaults are in use.</summary>
    ResetCorrupt,

    /// <summary>An older settings file loaded and was upgraded (a copy is kept as <c>.v{N}.bak</c>).</summary>
    Migrated,

    /// <summary>A settings file from a newer Envoy loaded best-effort (a copy is kept as <c>.v{N}.bak</c>).</summary>
    NewerVersion,

    /// <summary>Upgrading an older settings file failed; it loaded best-effort and the original is kept as <c>.v{N}.bak</c>.</summary>
    MigrationFailed,
}

/// <summary>The outcome of a load, for the startup toast and the diagnostics report.</summary>
/// <param name="FileVersion">The <c>version</c> read from disk (the current version for defaults).</param>
/// <param name="BackupPath">The copy that was loaded or kept (<c>.bak</c>, <c>.v{N}.bak</c>), if any.</param>
/// <param name="Error">A redacted description of what went wrong, or null.</param>
public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, int FileVersion,
                                        string? BackupPath, string? Error);

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON, encrypting [Secret] properties. Durable (REQ-REL-29…38): saves
/// are atomic and flushed, every good load refreshes a last-known-good <c>.bak</c>, a damaged file is kept as
/// <c>.broken</c> and the <c>.bak</c> is loaded instead, and <see cref="SuppressSaves"/> stops every later write so a
/// reset or "Delete all Envoy data" can't be undone by an exit save. UI thread only.
/// </summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly LogRedactor ErrorRedactor = new();

    private readonly ISecretProtector _protector;
    private readonly ILog _log;
    private readonly TimeProvider _clock;
    private readonly SettingsMigrator _migrator;
    private readonly int _currentVersion;
    private volatile bool _suppressed;

    public SettingsStore(string path, ISecretProtector protector)
        : this(path, protector, null)
    {
    }

    public SettingsStore(string path, ISecretProtector protector, ILog? log, TimeProvider? clock = null,
                         IReadOnlyList<ISettingsMigration>? migrations = null)
        : this(path, protector, log, clock, migrations, AppSettings.CurrentVersion)
    {
    }

    /// <summary>For tests: a store whose schema version is <paramref name="currentVersion"/> instead of the shipped one.</summary>
    internal SettingsStore(string path, ISecretProtector protector, ILog? log, TimeProvider? clock,
                           IReadOnlyList<ISettingsMigration>? migrations, int currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _log = log ?? NullLog.Instance;
        _clock = clock ?? TimeProvider.System;
        _currentVersion = currentVersion;
        _migrator = new SettingsMigrator(migrations ?? SettingsMigrations.All, currentVersion);
    }

    public string Path { get; }

    /// <summary>The only place in Envoy that names the roaming application-data folder (REQ-REL-44).</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Envoy", "settings.json");

    /// <summary>The last settings that loaded correctly.</summary>
    public string LastKnownGoodPath => Path + ".bak";

    /// <summary>A damaged settings file, kept for troubleshooting.</summary>
    public string BrokenPath => Path + ".broken";

    /// <summary>Scratch file for atomic writes (saves and the <c>.bak</c> refresh; both run on the UI thread).</summary>
    private string TempPath => Path + ".tmp";

    /// <summary>True once <see cref="SuppressSaves"/> has been called; <see cref="Save"/> then writes nothing.</summary>
    public bool SavesSuppressed => _suppressed;

    public AppSettings Load() => LoadWithReport().Settings;

    /// <summary>Loads the settings and says how (REQ-REL-33/34). Never throws for a missing, unreadable or damaged file.</summary>
    public SettingsLoadResult LoadWithReport()
    {
        if (!File.Exists(Path)) return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Defaults, _currentVersion, null, null);

        try
        {
            var parsed = Parse(File.ReadAllText(Path), Path, isBackup: false);
            if (parsed.Status is SettingsLoadStatus.Loaded or SettingsLoadStatus.Migrated or SettingsLoadStatus.NewerVersion)
                RefreshLastKnownGood();
            return parsed;
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            var error = Describe(ex);
            _log.Warn("Settings file could not be loaded; trying the last known good copy", null, LogField.Of("error", error));
            CopyQuietly(Path, BrokenPath);
            try
            {
                if (File.Exists(LastKnownGoodPath))
                {
                    var recovered = Parse(File.ReadAllText(LastKnownGoodPath), LastKnownGoodPath, isBackup: true);
                    _log.Info("Settings restored from the last known good copy");
                    return recovered with { Status = SettingsLoadStatus.RecoveredFromBackup, BackupPath = LastKnownGoodPath, Error = error };
                }
            }
            catch (Exception bakEx) when (IsLoadFailure(bakEx))
            {
                _log.Warn("The last known good settings could not be loaded either", null, LogField.Of("error", Describe(bakEx)));
            }
            _log.Warn("Settings reset to defaults");
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.ResetCorrupt, _currentVersion, null, error);
        }
    }

    /// <summary>
    /// Writes <paramref name="settings"/> atomically (temp file, flush to disk, move) with <c>version</c> =
    /// <see cref="AppSettings.CurrentVersion"/> and secrets encrypted. Does nothing once saves are suppressed.
    /// </summary>
    /// <exception cref="IOException">The write failed (propagates to AppHost.SaveNow, which logs it).</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is read-only.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_suppressed)
        {
            _log.Info("Settings save skipped: saving is suppressed for this session");
            return;
        }

        // Round-trip through JSON to get a deep copy, then encrypt secrets on the copy only.
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
        copy.Version = _currentVersion;
        TransformSecrets(copy, (_, value) => _protector.Protect(value));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(copy, JsonOptions));
        using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(TempPath, Path, overwrite: true);
    }

    /// <summary>Stops every later <see cref="Save"/> on this instance. Irreversible.</summary>
    public void SuppressSaves()
    {
        if (_suppressed) return;
        _suppressed = true;
        _log.Info("Settings saving suppressed for this session");
    }

    /// <summary>
    /// Suppresses saving, copies <c>settings.json</c> to <c>settings.json.reset-{yyyyMMdd-HHmmss}.bak</c> and deletes it
    /// (REQ-REL-38). Returns the copy's path, or null when there was no settings file.
    /// </summary>
    /// <exception cref="IOException">The copy or the delete failed; saving stays suppressed.</exception>
    public string? ResetToDefaults()
    {
        SuppressSaves();
        if (!File.Exists(Path)) return null;
        var stamp = _clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path + ".reset-" + stamp + ".bak";
        File.Copy(Path, backup, overwrite: true);
        File.Delete(Path);
        _log.Info("Settings reset to defaults by the user");
        return backup;
    }

    private SettingsLoadResult Parse(string text, string source, bool isBackup)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("The settings file is not a JSON object.");
        var version = SettingsMigrator.ReadVersion(root);
        var status = SettingsLoadStatus.Loaded;
        string? versionBackup = null;
        string? error = null;

        if (version < _currentVersion)
        {
            versionBackup = isBackup ? null : KeepVersionCopy(source, version);
            try
            {
                var steps = _migrator.Migrate(root, version);
                _log.Info("Settings upgraded", LogField.Of("from", version), LogField.Of("to", _currentVersion), LogField.Of("steps", steps.Count));
                status = SettingsLoadStatus.Migrated;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = Describe(ex);
                _log.Warn("Settings upgrade failed; loading the file as it is", null, LogField.Of("from", version), LogField.Of("error", error));
                root = JsonNode.Parse(text)!.AsObject();
                status = SettingsLoadStatus.MigrationFailed;
            }
        }
        else if (version > _currentVersion)
        {
            versionBackup = isBackup ? null : KeepVersionCopy(source, version);
            _log.Warn("Settings come from a newer Envoy; loading what this version understands", null,
                LogField.Of("fileVersion", version), LogField.Of("currentVersion", _currentVersion));
            status = SettingsLoadStatus.NewerVersion;
        }

        var settings = root.Deserialize<AppSettings>(JsonOptions) ?? throw new JsonException("The settings file is empty.");
        var repaired = SettingsSanitizer.RepairNulls(settings);
        if (repaired.Count > 0)
            _log.Warn("Missing settings sections were reset to defaults", null, LogField.Of("sections", string.Join(",", repaired)));
        TransformSecrets(settings, Unprotect);
        return new SettingsLoadResult(settings, status, version, versionBackup, error);
    }

    /// <summary>Copies the file byte-for-byte to <c>settings.json.v{N}.bak</c> once (REQ-REL-31/32).</summary>
    private string? KeepVersionCopy(string source, int version)
    {
        var target = Path + ".v" + version.ToString(CultureInfo.InvariantCulture) + ".bak";
        if (File.Exists(target)) return target;
        return CopyQuietly(source, target) ? target : null;
    }

    /// <summary>Copies <c>settings.json</c> to <c>.bak</c> through the temp file, so a crash never leaves a torn <c>.bak</c>.</summary>
    private void RefreshLastKnownGood()
    {
        try
        {
            File.Copy(Path, TempPath, overwrite: true);
            File.Move(TempPath, LastKnownGoodPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not refresh the last known good settings copy", ex);
        }
    }

    private bool CopyQuietly(string from, string to)
    {
        try
        {
            if (!File.Exists(from)) return false;
            File.Copy(from, to, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not copy a settings file", ex, LogField.Of("to", System.IO.Path.GetFileName(to)));
            return false;
        }
    }

    private static bool IsLoadFailure(Exception ex) =>
        ex is JsonException or InvalidOperationException or NotSupportedException or IOException
            or UnauthorizedAccessException or FormatException or ArgumentException;

    private static string Describe(Exception ex) => ErrorRedactor.Redact(ex.GetType().Name + ": " + ex.Message);

    private string Unprotect(string propertyName, string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (Exception)
        {
            // Settings copied from another PC/user can't be decrypted; the user just re-enters the key (REQ-REL-37).
            _log.Warn("A saved key could not be decrypted; enter it again", null, LogField.Of("property", propertyName));
            return "";
        }
    }

    private static void TransformSecrets(object node, Func<string, string, string> transform)
    {
        foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType == typeof(string) && prop.GetCustomAttribute<SecretAttribute>() is not null)
            {
                var value = (string?)prop.GetValue(node);
                if (!string.IsNullOrEmpty(value)) prop.SetValue(node, transform(prop.Name, value));
            }
            else if (prop.PropertyType.IsClass && prop.PropertyType.Namespace == typeof(AppSettings).Namespace)
            {
                if (prop.GetValue(node) is { } child) TransformSecrets(child, transform);
            }
        }
    }
}
