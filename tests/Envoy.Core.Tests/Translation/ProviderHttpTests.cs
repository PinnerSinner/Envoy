using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Providers;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class ProviderHttpTests
{
    [Theory]
    [InlineData(400, TranslationErrorKind.Unsupported)]
    [InlineData(401, TranslationErrorKind.Auth)]
    [InlineData(403, TranslationErrorKind.Auth)]
    [InlineData(404, TranslationErrorKind.Unsupported)]
    [InlineData(408, TranslationErrorKind.Transient)]
    [InlineData(413, TranslationErrorKind.Unsupported)]
    [InlineData(414, TranslationErrorKind.Unsupported)]
    [InlineData(422, TranslationErrorKind.Unsupported)]
    [InlineData(429, TranslationErrorKind.RateLimited)]
    [InlineData(456, TranslationErrorKind.QuotaExceeded)]
    [InlineData(500, TranslationErrorKind.Transient)]
    [InlineData(502, TranslationErrorKind.Transient)]
    [InlineData(503, TranslationErrorKind.Transient)]
    [InlineData(504, TranslationErrorKind.Transient)]
    [InlineData(529, TranslationErrorKind.Transient)]
    public void Classify_maps_status_codes(int status, TranslationErrorKind kind) =>
        Assert.Equal(kind, ProviderHttp.Classify(status));

    [Fact]
    public void ParseRetryAfter_accepts_seconds_and_http_date()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        using var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.TryAddWithoutValidation("Retry-After", "2");
        Assert.Equal(TimeSpan.FromSeconds(2), ProviderHttp.ParseRetryAfter(seconds, now));

        using var date = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        date.Headers.TryAddWithoutValidation("Retry-After", now.AddSeconds(30).ToString("R"));
        Assert.Equal(TimeSpan.FromSeconds(30), ProviderHttp.ParseRetryAfter(date, now));

        using var none = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Null(ProviderHttp.ParseRetryAfter(none, now));
    }

    [Fact]
    public void ParseRetryAfter_clamps_to_one_hour()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        using var huge = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        huge.Headers.TryAddWithoutValidation("Retry-After", "99999");
        Assert.Equal(TimeSpan.FromSeconds(3600), ProviderHttp.ParseRetryAfter(huge, now));

        using var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.TryAddWithoutValidation("Retry-After", now.AddMinutes(-5).ToString("R"));
        Assert.Equal(TimeSpan.Zero, ProviderHttp.ParseRetryAfter(past, now));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>unusual traffic</body></html>")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("")]
    public void Parse_wraps_json_errors_as_BadResponse(string body)
    {
        foreach (var parse in new Func<string, TranslationResult>[]
                 {
                     b => GoogleFreeTranslator.Parse(b, "google-free"),
                     b => EdgeFreeTranslator.Parse(b, "edge-free"),
                     b => DeepLTranslator.Parse(b, "deepl"),
                     b => LibreTranslateTranslator.Parse(b, "libretranslate"),
                 })
        {
            var ex = Assert.Throws<TranslationException>(() => ProviderHttp.Parse("google-free", body, parse));
            Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
            Assert.Equal("Google Translate (free) sent a reply Envoy could not read.", ex.Message);
            if (body.Length > 2) Assert.DoesNotContain(body[..3], ex.Message, StringComparison.Ordinal);
        }
    }
}

public class TranslationErrorTextTests
{
    private static readonly Regex Placeholder = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);

    public static TheoryData<string, string> Templates()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in new[] { typeof(TranslationErrorText), typeof(ProviderHealthText), typeof(TranslationCacheText) })
        {
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                data.Add(type.Name + "." + f.Name, (string)f.GetRawConstantValue()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Templates_have_named_placeholders_only(string name, string template)
    {
        Assert.False(template.StartsWith("Error", StringComparison.OrdinalIgnoreCase), name + " starts with Error");
        Assert.DoesNotContain("Error:", template, StringComparison.Ordinal);
        foreach (Match m in Placeholder.Matches(template))
        {
            var body = m.Groups[1].Value.Split(':')[0];
            Assert.Matches("^[a-z][a-zA-Z]*$", body);
        }
    }

    [Fact]
    public void Every_kind_has_a_whole_sentence()
    {
        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
        {
            var text = TranslationErrorText.ForKind(kind, ProviderCatalog.DeepL);
            Assert.StartsWith("DeepL", text, StringComparison.Ordinal);
            Assert.EndsWith(".", text, StringComparison.Ordinal);
        }
        foreach (var state in Enum.GetValues<ProviderState>())
            Assert.EndsWith(".", TranslationErrorText.ForSkip(state, ProviderCatalog.GoogleFree), StringComparison.Ordinal);
        Assert.Equal("No translation within 15 s.", TranslationErrorText.ForTimeout(TimeSpan.FromSeconds(15)));
    }
}
