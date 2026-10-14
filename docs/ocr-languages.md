# OCR languages

Envoy reads chat with the text recognition engine built into Windows 10 and 11 (`Windows.Media.Ocr`). It's free,
offline and fast, typically 20–60 ms per read. It can only recognise scripts whose **OCR language pack** is
installed.

## Which ones do I need?

Your own language is always used. Add the scripts your opponents write in:

| Opponents write | Install | Notes |
| --- | --- | --- |
| Chinese (mainland) | Chinese (Simplified, China) | The most common need in AoE IV and AoE II ranked |
| Chinese (Taiwan / HK) | Chinese (Traditional, Taiwan) | |
| Korean | Korean | |
| Japanese | Japanese | Also reads kanji |
| Russian, Ukrainian | Russian | Reads all Cyrillic |
| Spanish, Portuguese, German, French, Polish, Turkish… | English is enough | Latin script is shared |
| Vietnamese | Vietnamese | For the accents |

Each extra language adds one OCR pass per read. Four or five is fine on any modern PC.

## Installing

**From Envoy:** under **Reading Chat → Languages to recognise**, click **Install** next to the language. Windows
asks for administrator permission and runs:

```powershell
Add-WindowsCapability -Online -Name "Language.OCR~~~zh-CN~0.0.1.0"
```

**From Windows Settings:** go to *Time & language → Language & region → Add a language*, choose the language, and
make sure **Optical character recognition** is included. You don't need the display language or speech parts.

**From PowerShell (admin):**

```powershell
# See what's available / installed
Get-WindowsCapability -Online | Where-Object Name -like 'Language.OCR*'
# Install Chinese (Simplified), Korean, Japanese, Russian
'zh-CN','ko-KR','ja-JP','ru-RU' | ForEach-Object { Add-WindowsCapability -Online -Name "Language.OCR~~~$_~0.0.1.0" }
```

Restart Envoy afterwards.

## How Envoy combines languages

Every installed engine you've ticked reads the same captured image. For each line on screen, Envoy keeps the reading
from the engine that recognised the most characters of its own script. The Chinese engine wins on Chinese lines, the
Korean engine on Korean lines, and your own-language engine on Latin lines. Mixed chats therefore read correctly
without you choosing anything.

Envoy also removes the spaces Windows OCR inserts between Chinese and Japanese characters (`你 好` → `你好`), and
tolerates small misreads between frames, so a line is only translated once.

## Improving accuracy

Open **Reading Chat → Live view** with chat on screen, then adjust:

- **Enlarge before reading.** 2× is the default. Small chat fonts at 1080p often read better at 2.5–3×.
- **Contrast.** Raise it if letters run together.
- **Invert colours.** Windows OCR prefers dark text on a light background. Try it if light-on-dark chat reads poorly.
- **The chat area.** Tighter is better. Avoid including the minimap, portraits or the resource bar.
