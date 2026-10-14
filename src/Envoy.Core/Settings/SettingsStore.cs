using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, encrypting [Secret] properties.</summary>
public sealed class SettingsStore(string path, ISecretProtector protector)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Envoy", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonOptions) ?? new AppSettings();
            TransformSecrets(settings, Unprotect);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file shouldn't stop the app from starting; keep a copy for troubleshooting.
            TryBackup();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        // Round-trip through JSON to get a deep copy, then encrypt secrets on the copy only.
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
        TransformSecrets(copy, protector.Protect);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(temp, Path, overwrite: true);
    }

    private string Unprotect(string value)
    {
        try
        {
            return protector.Unprotect(value);
        }
        catch
        {
            // Settings copied from another PC/user can't be decrypted; the user just re-enters the key.
            return "";
        }
    }

    private static void TransformSecrets(object node, Func<string, string> transform)
    {
        foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType == typeof(string) && prop.GetCustomAttribute<SecretAttribute>() is not null)
            {
                var value = (string?)prop.GetValue(node);
                if (!string.IsNullOrEmpty(value)) prop.SetValue(node, transform(value));
            }
            else if (prop.PropertyType.IsClass && prop.PropertyType.Namespace == typeof(AppSettings).Namespace)
            {
                if (prop.GetValue(node) is { } child) TransformSecrets(child, transform);
            }
        }
    }

    private void TryBackup()
    {
        try
        {
            if (File.Exists(Path)) File.Copy(Path, Path + ".broken", overwrite: true);
        }
        catch
        {
            // best effort
        }
    }
}
