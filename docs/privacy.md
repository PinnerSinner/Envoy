# Privacy

Envoy has no account, no telemetry and no crash upload. This page lists what it keeps on your PC and how to remove
it.

## What Envoy stores on your PC

Everything lives in one folder, `%APPDATA%\Envoy` (About › **Open settings folder** opens it). Nothing else on your PC
is changed, apart from the optional "Start with Windows" entry in your user's Run key.

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
| `error.log` | Error log from Envoy 0.1. |

The `.bak` copies hold your API keys encrypted the same way as `settings.json`, so only your Windows account can read
them.

### Saved translations

**Remember translations between sessions** (Translation page) is off unless you turn it on. When it is on,
`translation-cache.json` keeps recent translations so a repeated line like "gg wp" is translated instantly and, with a
paid provider, for free. The file holds translations of chat lines you received, in your language, plus a one-way
fingerprint of each original line; reply-box translations (what you type and its back-translation) are never saved.
It never leaves this PC. The fingerprint stops casual reading of the original lines, but someone with the file could
still guess short, common ones, so treat the file like your chat transcripts.
It holds at most 2,000 entries (and 1 MiB), forgets entries after 30 days, and never stores messages longer than 200
characters. Turning the option off, or **Clear saved translations**, deletes the file.

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
