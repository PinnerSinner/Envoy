using System.Reflection;

namespace Envoy.Core.Settings;

/// <summary>
/// Replaces <c>null</c> sub-objects, lists, dictionaries and strings in loaded settings with the defaults of a fresh
/// instance (REQ-REL-35), so a hand-edited <c>"capture": null</c> can't crash the app later.
/// </summary>
public static class SettingsSanitizer
{
    /// <summary>Repairs <paramref name="settings"/> in place and returns the dotted paths it repaired.</summary>
    public static IReadOnlyList<string> RepairNulls(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var repaired = new List<string>();
        Repair(settings, "", repaired);
        return repaired;
    }

    private static void Repair(object node, string prefix, List<string> repaired)
    {
        var type = node.GetType();
        object? defaults = null;
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType.IsValueType) continue;
            var value = prop.GetValue(node);
            if (value is null)
            {
                defaults ??= Activator.CreateInstance(type);
                var fallback = prop.GetValue(defaults);
                if (fallback is null) continue; // null by design (e.g. Usage)
                prop.SetValue(node, fallback);
                repaired.Add(prefix + prop.Name);
                value = fallback;
            }
            if (IsSettingsClass(prop.PropertyType)) Repair(value, prefix + prop.Name + ".", repaired);
        }
    }

    internal static bool IsSettingsClass(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == typeof(AppSettings).Namespace
        && type.GetConstructor(Type.EmptyTypes) is not null;
}
