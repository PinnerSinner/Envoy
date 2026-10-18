using System.Collections;
using System.Reflection;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Settings;

/// <summary>Every [Secret], [Personal] and [Location] property is masked in diagnostics (contract §7).</summary>
public class SettingsRedactionTests
{
    private static bool IsMarked(PropertyInfo p) =>
        p.GetCustomAttribute<SecretAttribute>() is not null || p.GetCustomAttribute<PersonalAttribute>() is not null
        || p.GetCustomAttribute<LocationAttribute>() is not null;

    /// <summary>Fills every marked property reachable from <paramref name="node"/> with distinctive values; returns them.</summary>
    private static List<string> FillMarked(object node, string path = "")
    {
        var values = new List<string>();
        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            var tag = "QQ" + (path + p.Name).Replace(".", "", StringComparison.Ordinal) + "QQ";
            if (IsMarked(p))
            {
                if (p.PropertyType == typeof(string))
                {
                    p.SetValue(node, tag);
                    values.Add(tag);
                }
                else if (p.PropertyType == typeof(List<string>))
                {
                    p.SetValue(node, new List<string> { tag + "1", tag + "2" });
                    values.Add(tag + "1");
                }
                else if (p.PropertyType == typeof(Dictionary<string, ScreenRegion>))
                {
                    p.SetValue(node, new Dictionary<string, ScreenRegion> { ["game"] = new(4321, 8765, 640, 210) });
                    values.Add("4321");
                    values.Add("8765");
                }
                else if (p.PropertyType == typeof(Dictionary<string, WindowRelativeRegion>))
                {
                    p.SetValue(node, new Dictionary<string, WindowRelativeRegion> { ["game"] = new(4322, 8766, 640, 210, 1920, 1080) });
                    values.Add("4322");
                    values.Add("8766");
                    values.Add("1920");
                }
                else if (p.PropertyType == typeof(ScreenRegion))
                {
                    p.SetValue(node, new ScreenRegion(4321, 8765, 640, 210));
                    values.Add("4321");
                }
                else
                {
                    Assert.Fail($"Add a fill rule for marked property {path}{p.Name} of type {p.PropertyType}.");
                }
            }
            else if (p.PropertyType.IsClass && p.PropertyType.Namespace == typeof(AppSettings).Namespace && p.GetValue(node) is { } child
                     && child is not IEnumerable)
            {
                values.AddRange(FillMarked(child, path + p.Name + "."));
            }
        }
        return values;
    }

    [Fact]
    public void Every_marked_property_is_masked()
    {
        var settings = new AppSettings();
        var values = FillMarked(settings);
        Assert.True(values.Count >= 8, "expected the [Secret], [Personal] and [Location] properties to be found");

        var json = SettingsRedaction.ToRedactedJson(settings);

        foreach (var value in values) Assert.DoesNotContain(value, json, StringComparison.Ordinal);
        Assert.Contains("\"myPlayerName\": \"[set]\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mutedPlayers\": \"[2 entries]\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deepLKey\": \"[set]\"", json, StringComparison.Ordinal);
        Assert.Contains("\"myLanguage\": \"en\"", json, StringComparison.Ordinal); // unmarked values stay
        Assert.Contains("\"motion\": \"Auto\"", json, StringComparison.Ordinal); // spec 09b: a preference, no marker needed
    }

    [Fact]
    public void Location_becomes_size()
    {
        var settings = new AppSettings();
        settings.Capture.Regions["aoe4"] = new ScreenRegion(100, 200, 600, 180);
        settings.Capture.Regions["any"] = new ScreenRegion(5, 6, 300, 90);
        var json = SettingsRedaction.ToRedactedJson(settings);
        Assert.Contains("\"aoe4\": \"600×180\"", json, StringComparison.Ordinal);
        Assert.Contains("\"any\": \"300×90\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_values_render_empty()
    {
        var json = SettingsRedaction.ToRedactedJson(new AppSettings());
        Assert.Contains("\"myPlayerName\": \"\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mutedPlayers\": \"[0 entries]\"", json, StringComparison.Ordinal);
        Assert.Contains("\"claudeKey\": \"\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretValues_returns_only_non_empty_secrets()
    {
        var settings = new AppSettings { MyPlayerName = "not a secret" };
        settings.Translation.DeepLKey = "deepl-value";
        settings.Translation.AwsSecretAccessKey = "aws-secret-value";
        Assert.Equal(["deepl-value", "aws-secret-value"], SettingsRedaction.SecretValues(settings).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public void HasSecret_reports_set_and_unset()
    {
        var settings = new AppSettings();
        settings.Translation.ClaudeKey = "sk-ant-x";
        settings.Translation.DeepLKey = "   ";
        Assert.True(SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.ClaudeKey)));
        Assert.False(SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.DeepLKey)));
        Assert.False(SettingsRedaction.HasSecret(settings, nameof(TranslationSettings.LibreTranslateKey)));
        Assert.False(SettingsRedaction.HasSecret(settings, nameof(AppSettings.MyPlayerName))); // not a [Secret]
    }

    [Fact]
    public void Markers_are_on_the_contract_properties()
    {
        Assert.NotNull(typeof(AppSettings).GetProperty(nameof(AppSettings.MyPlayerName))!.GetCustomAttribute<PersonalAttribute>());
        Assert.NotNull(typeof(AppSettings).GetProperty(nameof(AppSettings.MutedPlayers))!.GetCustomAttribute<PersonalAttribute>());
        Assert.NotNull(typeof(CaptureSettings).GetProperty(nameof(CaptureSettings.Regions))!.GetCustomAttribute<LocationAttribute>());
        Assert.NotNull(typeof(CaptureSettings).GetProperty(nameof(CaptureSettings.WindowRegions))!.GetCustomAttribute<LocationAttribute>());
    }

    [Fact]
    public void Window_relative_location_becomes_size()
    {
        var settings = new AppSettings();
        settings.Capture.WindowRegions["aoe2de"] = new WindowRelativeRegion(20, 850, 600, 150, 1920, 1080);
        var json = SettingsRedaction.ToRedactedJson(settings);
        Assert.Contains("\"aoe2de\": \"600×150\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("850", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1080", json, StringComparison.Ordinal);
    }
}
