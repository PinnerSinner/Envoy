# Privacy

Envoy has no account, no telemetry and no crash upload. This page says what leaves your PC, what Envoy keeps on it
and how to remove it.

## What leaves your PC

Envoy shows this in the setup guide's first step, on the War Room and under About › Privacy. **Translation stays off
until you confirm it** (tick "I understand what leaves my PC" in the setup guide, or press **Turn on translation** on
the War Room). Until then Envoy sends no chat or reply text anywhere: the overlay shows the original lines and says why.
When the data flows change, Envoy asks again.

- **Chat lines Envoy reads, and replies you translate**, go to the translation providers you chose on the Translation
  page, in order (your first choice, any fallback, and Google Translate (free) as the last resort). The default is
  Google Translate (free), which uses Google's unofficial keyless endpoint (`translate.googleapis.com`); Microsoft
  Translator (free) uses the keyless endpoint Microsoft Edge uses. Neither needs an account, and neither is an official
  API for apps like Envoy. Paid providers (DeepL, Claude, Amazon Translate) and your own LibreTranslate server receive
  the same text under your own account. Envoy sends the message text and the languages; it adds nothing
  about you, the game or your PC (the text itself can of course contain names).
- **Update checks** go to GitHub only (`api.github.com` and this project's release downloads), sending nothing but the
  request and the Envoy version. See [How updates work](installing.md#how-updates-work).
- **Text recognition (OCR) and speech** run on your PC with Windows' own engines and send nothing.
- **Links you click** (release notes, these docs) open in your browser.

There is no telemetry, no crash upload and no Envoy server.

### How to turn translation off

- Untick "I understand what leaves my PC" in the setup guide (About › **Run setup guide again**). Translation is off
  again, for chat and replies, until you confirm; nothing is sent in the meantime.
- Pause reading (tray › **Pause / resume reading**, or its hotkey) so no new chat is read or sent.
- Google Translate (free) is always the last resort in the chain. To keep text inside your own network, choose your
  own LibreTranslate server on the Translation page; Google is then used only when it fails.
- **Check for updates automatically** (Appearance › Startup & updates) turns the automatic GitHub checks off.

## What Envoy stores on your PC

Everything lives in one folder, `%APPDATA%\Envoy` (About › **Open settings folder** opens it). Nothing else on your PC
is changed, apart from the optional "Start with Windows" entry in your user's Run key and, if you used the installer,
the program and the installer's log (see [Installed copies](#installed-copies)).

| File | What it is |
| --- | --- |
| `settings.json` | Your settings; API keys are encrypted for your Windows account. |
| `settings.json.bak` | Last settings that loaded correctly. |
| `settings.json.v*.bak` | Copy kept before a settings upgrade. |
| `settings.json.reset-*.bak` | Copy kept when you reset settings. |
| `settings.json.broken` | A damaged settings file kept for troubleshooting. |
| `settings.json.tmp` | Left over if Envoy closed while saving. |
| `phrases.json` | Your own quick phrases. |
| `logs/envoy*.log` | Diagnostic log. |
| `logs/chat-*.txt` | Chat transcripts, if you turned them on. |
| `last-crash.json` | Details of the last crash. |
| `last-crash.json.tmp` | Left over if Envoy closed while recording a crash. |
| `session.json` | Start-up marker used to detect crashes. |
| `translation-cache.json` | Saved translations, if you turned on "Remember translations". |
| `translation-cache.json.tmp` | Left over if Envoy closed while saving translations. |
| `translation-cache.json.bad` | A damaged saved-translations file set aside. |
| `chat-formats.json` | Your own chat format profiles (you create this file). |
| `error.log` | Error log from Envoy 0.1. |

The `.bak` copies hold your API keys encrypted the same way as `settings.json`, so only your Windows account can read
them.

### Installed copies

If you installed Envoy with `Envoy-win-Setup.exe`, the program itself lives in `%LocalAppData%\Envoy`, and the
installer and updater (Velopack) keep a technical log in `%LocalAppData%\velopack` (`velopack_Envoy.log`; Envoy's own
update code adds to it). It records install, update and start-up steps with file paths, which include your Windows
user name; it never contains settings, keys or chat, and it never leaves your PC. It is not in the data folder, so
**Delete all Envoy data…** does not remove it: delete it by hand if you want. The portable `Envoy.exe` never creates
it.

### Saved translations

**Remember translations between sessions** (Translation page) is off unless you turn it on. When it is on,
`translation-cache.json` keeps recent translations so a repeated line like "gg wp" is translated instantly and, with a
paid provider, for free. The file holds translations of chat lines you received, in your language, plus a one-way
fingerprint of each original line; reply-box translations (what you type and its back-translation) are never saved.
It never leaves this PC. The fingerprint stops casual reading of the original lines, but someone with the file could
still guess short, common ones, so treat the file like your chat transcripts.
It holds at most 2,000 entries (and 1 MiB), forgets entries after 30 days, and never stores messages longer than 200
characters. Turning the option off, or **Clear saved translations**, deletes the file.

### Reply history and quick-phrase counts

Replies you recall with Up in the reply box are kept in memory only and forgotten when Envoy closes; they are never
saved or logged. To put your most-used quick phrases first, `settings.json` keeps, per reply language, the id of each
built-in or `phrases.json` phrase you sent and how often (at most 100 per language). It never keeps the text you typed.

### Chat formats and test captures

Envoy never writes `chat-formats.json`; it only reads it if you create one (see [chat formats](chat-formats.md)). The
game-notice patterns you type on the Reading Chat page are stored in `settings.json` and may contain player names, so
diagnostics show only how many there are.

**Copy as test capture** (Reading Chat › Live view) copies what OCR read from the chat area, with its positions, to the
clipboard so you can attach it to a bug report. It runs only when you click it and is never sent anywhere by Envoy, but
it contains chat and player names: anonymise it before you share it.

### The diagnostic log

`logs\envoy.log` records what Envoy did (start-up, settings loaded, a provider failing, errors) so a bug report can say
what went wrong. It never contains your API keys: every key you entered, and anything that looks like a key or a web
address's query, is replaced with `[redacted]` before it is written. Chat lines and player names appear only as their
length (`text=<12 chars>`) unless you turn on **Include chat text in logs** under About › Troubleshooting. Your Windows
user folder is written as `%USERPROFILE%`.

The log is capped at five files of 1 MiB each (`envoy.log`, `envoy.1.log` … `envoy.4.log`); the oldest is deleted when
a new one starts.

### Diagnostics you share

**Copy diagnostics** and **Export diagnostics…** (About › Troubleshooting) build a report on your PC. It leaves your PC
only if you paste or attach it somewhere. It lists versions, Windows, monitor sizes and scaling, the detected game,
the chat area's size (never its position), OCR languages, which providers have a key set (never the key), hotkeys
that failed, the settings with every key, name and position masked, and the last 50 log lines. The exported `.zip`
adds the diagnostic log files and `last-crash.json`; it never includes chat transcripts, `settings.json` or any `.bak`.

## Deleting everything

About › Troubleshooting › **Delete all Envoy data…** shows the files above that exist, then deletes exactly those,
removes the "Start with Windows" entry and restarts Envoy as if newly installed. Other files you put in the folder are
left alone. A file another program has open (for example a log open in an editor) is kept, and Envoy tells you which.
