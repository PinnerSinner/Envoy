using Envoy.Core.Chat;
using Envoy.Core.Diagnostics;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatFormatCatalogTests
{
    internal static string BuiltInJson => ChatFormatCatalog.ReadBuiltInJson();

    [Fact]
    public void Builtin_has_generic_only()
    {
        var catalog = ChatFormatCatalog.BuiltIn;
        var generic = Assert.Single(catalog.Profiles);
        Assert.Equal(ChatFormatProfile.GenericId, generic.Id);
        Assert.Same(generic, catalog.Generic);
        Assert.Empty(catalog.GameProfiles);
        Assert.Empty(catalog.Errors);
        Assert.Empty(generic.SystemPatterns);
        Assert.True(generic.StitchEnabled);
        Assert.Equal(new StitchOptions(), generic.Stitching);
    }

    [Fact]
    public void User_file_adds_and_replaces_profiles()
    {
        var catalog = ChatFormatCatalog.Parse(BuiltInJson, """
            {
              // comments and trailing commas are fine
              "Profiles": [
                { "id": "aoe4-en", "name": "AoE IV (English)", "separators": [":"], },
                { "id": "generic", "name": "My generic", "maxSenderLength": 20 },
              ],
              "games": { "aoe4": "aoe4-en" },
              "somethingElse": 1,
            }
            """);
        Assert.Empty(catalog.Errors);
        Assert.Equal(["generic", "aoe4-en"], catalog.Profiles.Select(p => p.Id));
        Assert.Equal("My generic", catalog.Generic.Name);
        Assert.Equal(20, catalog.Generic.MaxSenderLength);
        Assert.Equal("aoe4-en", catalog.GameProfiles["aoe4"]);
    }

    [Fact]
    public void Inherits_fills_missing_fields()
    {
        var catalog = ChatFormatCatalog.Parse(BuiltInJson, """
            { "profiles": [
                { "id": "base", "name": "Base", "separators": [">"], "stitching": { "maxContinuationLines": 2 } },
                { "id": "child", "inherits": "base", "name": "Child" },
                { "id": "plain", "name": "Plain" } ] }
            """);
        Assert.Empty(catalog.Errors);
        var child = catalog.Find("child")!;
        Assert.Equal([">"], child.Separators);
        Assert.Equal(2, child.Stitching.MaxContinuationLines);
        Assert.Equal(1.0, child.Stitching.MaxGapFactor);
        Assert.Equal("All", child.CanonicalChannel("everyone"));
        Assert.Equal([":", "："], catalog.Find("plain")!.Separators); // no "inherits": generic fills the gaps
    }

    [Fact]
    public void Bad_regex_field_falls_back_with_error()
    {
        var catalog = ChatFormatCatalog.Parse(BuiltInJson, """
            { "profiles": [ { "id": "bad", "name": "Bad", "timestamp": "([", "separators": [],
                "stitching": { "maxGapFactor": 9, "maxContinuationLines": 11, "heightTolerance": 0.4 },
                "systemPatterns": { "*": ["re:([", "*left*"] }, "inherits": "nope" } ] }
            """);
        var bad = catalog.Find("bad")!;
        Assert.Equal(ChatFormatProfile.Generic.TimestampPattern, bad.TimestampPattern);
        Assert.Equal([":", "："], bad.Separators);
        Assert.Equal(1.0, bad.Stitching.MaxGapFactor);
        Assert.Equal(4, bad.Stitching.MaxContinuationLines);
        Assert.Equal(0.4, bad.Stitching.HeightTolerance);
        Assert.Equal(["*left*"], bad.SystemPatterns["*"]);
        Assert.Equal(new[] { "timestamp", "separators", "maxGapFactor", "maxContinuationLines", "systemPatterns", "inherits" }.Order(),
            catalog.Errors.Select(e => e.Field!).Order());
        Assert.All(catalog.Errors, e => Assert.Equal(("bad", ChatFormatErrorKind.FieldInvalid), (e.Profile!, e.Kind)));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    public void Malformed_user_file_keeps_builtins(string json)
    {
        var catalog = ChatFormatCatalog.Parse(BuiltInJson, json);
        Assert.Single(catalog.Profiles);
        Assert.Equal(ChatFormatErrorKind.FileUnreadable, Assert.Single(catalog.Errors).Kind);
    }

    [Fact]
    public void Missing_or_unreadable_user_file_never_throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "envoy-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "chat-formats.json");
            var missing = ChatFormatCatalog.LoadWithUserFile(path, NullLog.Instance);
            Assert.Empty(missing.Errors);
            Assert.Single(missing.Profiles);

            File.WriteAllText(path, "{ \"profiles\": [ { \"id\": \"mine\", \"separators\": [\">\"] } ] }");
            var loaded = ChatFormatCatalog.LoadWithUserFile(path, NullLog.Instance);
            Assert.NotNull(loaded.Find("mine"));

            File.WriteAllText(path, new string(' ', (int)ChatFormatCatalog.MaxUserFileBytes + 1));
            Assert.Equal(ChatFormatErrorKind.FileUnreadable, Assert.Single(ChatFormatCatalog.LoadWithUserFile(path, NullLog.Instance).Errors).Kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_auto_uses_game_map_then_generic()
    {
        var catalog = ChatFormatCatalog.Parse(BuiltInJson, """{ "profiles": [ { "id": "aoe4-en" } ], "games": { "aoe4": "aoe4-en", "aoe2de": "missing" } }""");
        Assert.Equal("aoe4-en", catalog.Resolve(ChatFormatCatalog.Auto, "aoe4").Id);
        Assert.Equal("aoe4-en", catalog.Resolve(null, "AOE4").Id);
        Assert.Equal("generic", catalog.Resolve("auto", "aoe2de").Id);
        Assert.Equal("generic", catalog.Resolve("auto", "aoe3de").Id);
        Assert.Equal("generic", catalog.Resolve("auto", null).Id);
        Assert.Equal("aoe4-en", catalog.Resolve("aoe4-en", "aoe2de").Id);
    }

    [Fact]
    public void Resolve_unknown_id_is_generic() =>
        Assert.Same(ChatFormatCatalog.BuiltIn.Generic, ChatFormatCatalog.BuiltIn.Resolve("deleted-profile", "aoe4"));

    [Fact]
    public void Profile_system_patterns_apply_for_my_language_and_star()
    {
        var catalog = ChatFormatCatalog.Parse(BuiltInJson, """
            { "profiles": [ { "id": "p", "systemPatterns": { "en": ["*resigned*"], "*": ["*joined*"], "de": ["*aufgegeben*"] } } ] }
            """);
        var p = catalog.Find("p")!;
        Assert.Equal(new[] { "*resigned*", "*joined*" }.Order(), p.SystemPatternsFor("en-US").Order());
        Assert.Equal(new[] { "*joined*", "*aufgegeben*" }.Order(), p.SystemPatternsFor("de").Order());
    }
}
