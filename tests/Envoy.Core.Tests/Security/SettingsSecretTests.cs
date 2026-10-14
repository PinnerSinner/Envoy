using System.Reflection;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Security;

/// <summary>
/// Credentials in settings must be <see cref="SecretAttribute"/>-marked and reachable by <see cref="SettingsStore"/>,
/// which encrypts them on save (REQ-QSC-34). If a later area puts a secret inside a collection, these tests fail until
/// the store learns to walk collections; that is intended.
/// </summary>
public class SettingsSecretTests
{
    private static readonly string[] CredentialSuffixes = ["Key", "KeyId", "Secret", "Token", "Password"];

    /// <summary>Properties named like credentials that are not secrets (key bindings).</summary>
    private static readonly HashSet<string> KeyBindingAllowlist = new(StringComparer.Ordinal) { "OpenChatKey" };

    private static IEnumerable<Type> SettingsTypes =>
        typeof(AppSettings).Assembly.GetTypes().Where(t => t.Namespace == typeof(AppSettings).Namespace);

    private static IEnumerable<PropertyInfo> PublicProperties(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static bool IsSecret(PropertyInfo p) => p.GetCustomAttribute<SecretAttribute>() is not null;

    /// <summary>The types SettingsStore.TransformSecrets walks: settings-namespace classes behind read/write, non-indexer properties.</summary>
    private static HashSet<Type> ReachableFromAppSettings()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([typeof(AppSettings)]);
        while (queue.TryDequeue(out var type))
        {
            if (!seen.Add(type)) continue;
            foreach (var p in PublicProperties(type))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (p.PropertyType.IsClass && p.PropertyType.Namespace == typeof(AppSettings).Namespace) queue.Enqueue(p.PropertyType);
            }
        }
        return seen;
    }

    /// <summary>Every [Secret] property reachable from a fresh AppSettings, with the object that holds it.</summary>
    private static List<(object Owner, PropertyInfo Property)> ReachableSecrets(object node)
    {
        var found = new List<(object, PropertyInfo)>();
        foreach (var p in PublicProperties(node.GetType()))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            if (p.PropertyType == typeof(string) && IsSecret(p)) found.Add((node, p));
            else if (p.PropertyType.IsClass && p.PropertyType.Namespace == typeof(AppSettings).Namespace && p.GetValue(node) is { } child)
                found.AddRange(ReachableSecrets(child));
        }
        return found;
    }

    [Fact]
    public void Credential_like_properties_are_marked_Secret()
    {
        var unmarked = SettingsTypes
            .SelectMany(t => PublicProperties(t).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.PropertyType == typeof(string))
            .Where(x => CredentialSuffixes.Any(s => x.Property.Name.EndsWith(s, StringComparison.Ordinal)))
            .Where(x => !KeyBindingAllowlist.Contains(x.Property.Name) && !IsSecret(x.Property))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();
        Assert.Empty(unmarked);
    }

    [Fact]
    public void Secret_properties_are_strings()
    {
        var wrong = SettingsTypes.SelectMany(PublicProperties).Where(p => IsSecret(p) && p.PropertyType != typeof(string))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}").ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void Saved_file_contains_no_plaintext_secret()
    {
        var settings = new AppSettings();
        var secrets = ReachableSecrets(settings);
        Assert.True(secrets.Count >= 5, $"Expected at least 5 [Secret] properties, found {secrets.Count}.");
        for (var i = 0; i < secrets.Count; i++) secrets[i].Property.SetValue(secrets[i].Owner, $"MARKER-{i}");

        var path = Path.Combine(Path.GetTempPath(), $"envoy-secret-test-{Guid.NewGuid():N}", "settings.json");
        try
        {
            new SettingsStore(path, new ReversingProtector()).Save(settings);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("MARKER-", text, StringComparison.Ordinal);
            Assert.Contains("enc:", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Secret_properties_are_reachable_by_store()
    {
        var reachable = ReachableFromAppSettings();
        var unreachable = typeof(AppSettings).Assembly.GetTypes()
            .Where(t => PublicProperties(t).Any(IsSecret))
            .Where(t => !reachable.Contains(t))
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(unreachable);
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + new string(plaintext.Reverse().ToArray());

        public string Unprotect(string protectedText) => new(protectedText[4..].Reverse().ToArray());
    }
}
