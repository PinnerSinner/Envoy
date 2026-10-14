namespace Envoy.App;

/// <summary>
/// App-level actions the view models may trigger. Implemented by <see cref="App"/>. Each area adds only its own block
/// (contract §3); this is the 01 block.
/// </summary>
public interface IShell
{
    // 01
    void SelectRegion();
    void ToggleOverlayEditMode();
    void OpenCompose();
    void ScanAndTranslate();
    void ExitApp();

    /// <summary>Opens a URL, a settings URI or a file/folder in the data folder with the shell.</summary>
    void OpenUrl(string urlOrDataPath);

    /// <summary>Installs the Windows OCR pack for an Envoy language code (asks for admin); opens Language settings if that fails.</summary>
    void InstallOcrPack(string languageTag);

    /// <summary>An information box, owned by the main window when it is open.</summary>
    void ShowInfo(string title, string message);
}
