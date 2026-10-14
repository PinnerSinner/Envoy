using Amazon;
using Amazon.Runtime;
using Amazon.Translate;
using Amazon.Translate.Model;
using Envoy.Core.Languages;

namespace Envoy.Core.Translation.Providers;

public sealed class AwsTranslator : ITranslator, IDisposable
{
    private readonly AmazonTranslateClient _client;

    public AwsTranslator(string accessKeyId, string secretAccessKey, string region)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
            throw new TranslationException("Amazon Translate needs an access key and secret (Settings → Translation).");
        _client = new AmazonTranslateClient(
            new BasicAWSCredentials(accessKeyId, secretAccessKey),
            RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(region) ? "us-east-1" : region));
    }

    public string Id => ProviderCatalog.Aws;
    public bool IsMetered => true;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.TranslateTextAsync(new TranslateTextRequest
            {
                Text = request.Text,
                SourceLanguageCode = request.SourceLanguage is null ? "auto" : ToAws(request.SourceLanguage),
                TargetLanguageCode = ToAws(request.TargetLanguage),
            }, cancellationToken).ConfigureAwait(false);
            return new TranslationResult(response.TranslatedText, LanguageCatalog.Normalize(response.SourceLanguageCode), Id);
        }
        catch (AmazonServiceException ex)
        {
            throw new TranslationException($"Amazon Translate: {ex.Message}", ex);
        }
    }

    private static string ToAws(string code) => code switch
    {
        "zh-CN" => "zh",
        "zh-TW" => "zh-TW",
        _ => code.Split('-')[0],
    };

    public void Dispose() => _client.Dispose();
}
