namespace Envoy.Core.Languages;

/// <summary>Builds the elevated PowerShell command that installs a Windows OCR language pack.</summary>
public static class OcrPackInstall
{
    /// <summary>Opened instead when the admin prompt is declined (or the language is unknown).</summary>
    public const string SettingsFallbackUri = "ms-settings:regionlanguage";

    /// <summary>
    /// <c>Language.OCR~~~{tag}~0.0.1.0</c>, where tag is the code for zh-CN/zh-TW (Windows capability names use
    /// those rather than script tags) and the catalog's OCR tag otherwise.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The tag contains anything other than ASCII letters, digits and '-' (it is spliced into a command run elevated).
    /// </exception>
    public static string CapabilityName(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var tag = language.Code is "zh-CN" or "zh-TW" ? language.Code : language.OcrTag;
        if (tag.Length == 0 || !tag.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
            throw new ArgumentException($"Unsafe OCR language tag '{tag}'.", nameof(language));
        return $"Language.OCR~~~{tag}~0.0.1.0";
    }

    /// <summary>The exact argument string passed to <c>powershell.exe</c> to install the pack for a language code.</summary>
    /// <exception cref="ArgumentException">The code is not in <see cref="LanguageCatalog"/>, or its tag is unsafe.</exception>
    public static string PowerShellArguments(string languageCode)
    {
        var language = LanguageCatalog.Find(languageCode)
            ?? throw new ArgumentException($"Unknown language code '{languageCode}'.", nameof(languageCode));
        var command = $"Add-WindowsCapability -Online -Name '{CapabilityName(language)}'; Write-Host 'Done - restart Envoy to use it.'; Start-Sleep 4";
        return $"-NoProfile -Command \"{command}\"";
    }
}
