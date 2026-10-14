namespace Envoy.App;

/// <summary>
/// App-level actions the view models may trigger. Implemented by <see cref="App"/>. Each area adds only its own block
/// (contract §3).
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

    // 02

    /// <summary>An OK/Cancel confirmation owned by the main window; true for OK. Only for actions the user started there.</summary>
    bool Confirm(string title, string message);

    /// <summary>Exits and starts Envoy again, in safe mode or normally.</summary>
    void Restart(bool safeMode);

    /// <summary>Confirms, then resets settings (keeping a copy) and restarts (REQ-REL-38).</summary>
    void ResetSettingsAndRestart();

    /// <summary>Confirms, then deletes every inventory file, removes the Run value and restarts (REQ-REL-44).</summary>
    void DeleteAllDataAndRestart();

    /// <summary>A save dialog in the Desktop folder; null when cancelled.</summary>
    string? ChooseExportPath(string defaultFileName);

    /// <summary>Puts text on the clipboard (3 tries 100 ms apart); a "clipboard busy" problem toast when that fails. False on failure.</summary>
    bool CopyText(string text);

    /// <summary>Shows a file selected in Explorer.</summary>
    void RevealFile(string path);
}
