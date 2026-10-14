using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Envoy.Core.Settings;

/// <summary>
/// The settings as they may appear in a diagnostics report (REQ-REL-23): <see cref="SecretAttribute"/> and
/// <see cref="PersonalAttribute"/> strings become <c>"[set]"</c> or <c>""</c>, their collections <c>"[N entries]"</c>,
/// and <see cref="LocationAttribute"/> regions only their width × height.
/// </summary>
public static class SettingsRedaction
{
    public const string SetMarker = "[set]";

    /// <summary>The store's options, but writing "×" and non-Latin names as themselves rather than \u escapes.</summary>
    private static readonly JsonSerializerOptions ReportJson = new(SettingsStore.JsonOptions)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Every non-empty <see cref="SecretAttribute"/> value, for <c>LogRedactor.SetSecrets</c>.</summary>
    public static IReadOnlyList<string> SecretValues(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = new List<string>();
        Walk(settings, (prop, owner) =>
        {
            if (prop.PropertyType == typeof(string) && prop.GetCustomAttribute<SecretAttribute>() is not null
                && prop.GetValue(owner) is string { Length: > 0 } value)
                values.Add(value);
        });
        return values;
    }

    /// <summary>True when the <see cref="SecretAttribute"/> property named <paramref name="propertyName"/> is non-blank.</summary>
    public static bool HasSecret(AppSettings settings, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var found = false;
        Walk(settings, (prop, owner) =>
        {
            if (prop.Name == propertyName && prop.GetCustomAttribute<SecretAttribute>() is not null
                && prop.GetValue(owner) is string value && !string.IsNullOrWhiteSpace(value))
                found = true;
        });
        return found;
    }

    /// <summary>Indented JSON of <paramref name="settings"/> with every marked property masked.</summary>
    public static string ToRedactedJson(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = JsonSerializer.SerializeToNode(settings, SettingsStore.JsonOptions)!.AsObject();
        Redact(settings, root);
        return root.ToJsonString(ReportJson);
    }

    /// <summary>"600×200".</summary>
    public static string Size(ScreenRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Size(region.Width, region.Height);
    }

    private static string Size(int width, int height) =>
        width.ToString(CultureInfo.InvariantCulture) + "×" + height.ToString(CultureInfo.InvariantCulture);

    private static void Redact(object node, JsonObject json)
    {
        foreach (var prop in Properties(node.GetType()))
        {
            var name = JsonName(prop);
            if (!json.ContainsKey(name)) continue;
            var value = prop.GetValue(node);
            var masked = prop.GetCustomAttribute<SecretAttribute>() is not null || prop.GetCustomAttribute<PersonalAttribute>() is not null;
            if (masked)
            {
                json[name] = value switch
                {
                    null => null,
                    string s => string.IsNullOrEmpty(s) ? "" : SetMarker,
                    ICollection c => "[" + c.Count.ToString(CultureInfo.InvariantCulture) + " entries]",
                    _ => SetMarker,
                };
            }
            else if (prop.GetCustomAttribute<LocationAttribute>() is not null)
            {
                json[name] = value switch
                {
                    null => null,
                    ScreenRegion r => Size(r),
                    WindowRelativeRegion r => Size(r.Width, r.Height),
                    IDictionary d => SizesOf(d),
                    ICollection c => "[" + c.Count.ToString(CultureInfo.InvariantCulture) + " entries]",
                    _ => SetMarker,
                };
            }
            else if (value is not null && SettingsSanitizer.IsSettingsClass(prop.PropertyType) && json[name] is JsonObject child)
            {
                Redact(value, child);
            }
        }
    }

    private static JsonObject SizesOf(IDictionary regions)
    {
        var result = new JsonObject();
        foreach (DictionaryEntry entry in regions)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "";
            result[key] = entry.Value switch
            {
                ScreenRegion r => Size(r),
                WindowRelativeRegion r => Size(r.Width, r.Height),
                _ => SetMarker,
            };
        }
        return result;
    }

    private static void Walk(object node, Action<PropertyInfo, object> visit)
    {
        foreach (var prop in Properties(node.GetType()))
        {
            visit(prop, node);
            if (SettingsSanitizer.IsSettingsClass(prop.PropertyType) && prop.GetValue(node) is { } child) Walk(child, visit);
        }
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    private static string JsonName(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? SettingsStore.JsonOptions.PropertyNamingPolicy?.ConvertName(prop.Name)
        ?? prop.Name;
}
