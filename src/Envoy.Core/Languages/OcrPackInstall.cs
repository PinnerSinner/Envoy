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
    public static string PowerShellArguments(string languageCode) => PowerShellArguments([languageCode]);

    /// <summary>
    /// One elevated PowerShell run that installs every pack in <paramref name="languageCodes"/>, one after another,
    /// saying in plain words which one it is on and that each can take a few minutes (Windows' own progress bar,
    /// "Operation Running [ooo]", looks frozen and is hidden). Language names and capability names are checked
    /// against a safe character set, so nothing from outside can reach the command line.
    /// </summary>
    public static string PowerShellArguments(IEnumerable<string> languageCodes)
    {
        ArgumentNullException.ThrowIfNull(languageCodes);
        var languages = languageCodes
            .Select(code => LanguageCatalog.Find(code) ?? throw new ArgumentException($"Unknown language code '{code}'.", nameof(languageCodes)))
            .DistinctBy(CapabilityName)
            .ToList();
        if (languages.Count == 0) throw new ArgumentException("No language to install.", nameof(languageCodes));
        var names = string.Join(",", languages.Select(l => Quote(SafeName(l.EnglishName))));
        var caps = string.Join(",", languages.Select(l => Quote(CapabilityName(l))));
        var script = string.Join("; ",
            "$ProgressPreference = 'SilentlyContinue'",
            "$Host.UI.RawUI.WindowTitle = 'Envoy: installing text recognition'",
            $"$names = @({names})",
            $"$caps = @({caps})",
            "Write-Host 'Envoy is installing Windows text recognition for other scripts.'",
            "Write-Host 'Each language can take a few minutes. You can keep using your PC; Envoy notices them by itself.'",
            "Write-Host ''",
            "for ($i = 0; $i -lt $caps.Count; $i++) { "
                + "Write-Host ('[' + ($i + 1) + ' of ' + $caps.Count + '] ' + $names[$i] + ' ...'); "
                + "try { Add-WindowsCapability -Online -Name $caps[$i] -ErrorAction Stop | Out-Null; Write-Host '    Installed.' -ForegroundColor Green } "
                + "catch { Write-Host ('    Not installed: ' + $_.Exception.Message) -ForegroundColor Yellow } }",
            "Write-Host ''",
            "Write-Host 'Finished. This window closes in 15 seconds.'",
            "Start-Sleep 15");
        return $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"";
    }

    private static string Quote(string value) => "'" + value + "'";

    private static string SafeName(string name)
    {
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '(' or ')' or '-'))
            throw new ArgumentException($"Unsafe language name '{name}'.", nameof(name));
        return name;
    }
}
