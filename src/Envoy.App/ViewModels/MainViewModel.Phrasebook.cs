using Envoy.Core.Languages;
using Envoy.Core.Phrases;

namespace Envoy.App.ViewModels;

/// <summary>Phrasebook page.</summary>
public sealed partial class MainViewModel
{
    private Language? _phrasePreviewLanguage;
    private IReadOnlyList<PhrasePreviewRow> _phraseRows = [];

    /// <summary>The "Preview in" language. Not a setting.</summary>
    public Language? PhrasePreviewLanguage
    {
        get => _phrasePreviewLanguage;
        set
        {
            if (SetProperty(ref _phrasePreviewLanguage, value)) RefreshPhrasePreview();
        }
    }

    public IReadOnlyList<PhrasePreviewRow> PhraseRows
    {
        get => _phraseRows;
        private set => SetProperty(ref _phraseRows, value);
    }

    public void RefreshPhrasePreview() =>
        PhraseRows = PhrasePreview.Build(Host.Phrases, Settings.MyLanguage, PhrasePreviewLanguage?.Code ?? "zh-CN");
}
