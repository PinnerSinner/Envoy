# Phrasebook

The phrasebook is a set of hand-translated messages for common moments. Phrases show as one-click chips in the reply
box. If you type a phrase exactly, Envoy sends the human translation instead of a machine one, for free and offline.

## Built-in phrases

16 phrases in 13 languages: English, Chinese (Simplified and Traditional), Korean, Japanese, Russian, Spanish,
Portuguese, German, French, Turkish, Vietnamese and Polish. They cover greetings (gl hf, hello, "I'm using a
translator"), the end of a game (gg wp, surrender, rematch, bye), courtesy (thanks, sorry, nice move), match
situations (wait, pause, lag) and team play (where should we attack, help, coming).

The source is `src/Envoy.Core/Phrases/phrases.json`. Corrections from native speakers are very welcome.

## Adding your own

**Phrasebook → Edit my phrases** creates and opens `%APPDATA%\Envoy\phrases.json`:

```json
[
  {
    "id": "my-first-phrase",
    "category": "Mine",
    "icon": "★",
    "text": {
      "en": "Nice to meet you!",
      "zh-CN": "很高兴认识你！",
      "ko": "만나서 반가워요!"
    }
  }
]
```

- `id`: any unique name. Use a built-in phrase's id (e.g. `glhf`) to replace that phrase.
- `text`: one entry per language, keyed by Envoy's language codes: `en`, `zh-CN`, `zh-TW`, `ko`, `ja`, `ru`, `uk`,
  `es`, `pt`, `de`, `fr`, `it`, `pl`, `tr`, `vi`, `nl`, `cs`, `sv`, `ro`, `hu`, `id`, `ar`, `th`, `el`.
- Languages you leave out fall back to machine translation when you send the phrase.

Save the file, then press OK in Envoy to reload.

### Limits

Phrase text is typed into game chat, so Envoy cleans and bounds the file (a shared file shouldn't be able to type
anything odd):

- A `phrases.json` larger than 1 MiB is ignored; only the built-in phrases load.
- At most 500 of your phrases are loaded. Phrases with an empty `id` are skipped.
- `id` and `category` are cut to 64 characters, `icon` to 16, and each text to 500.
- Line breaks and tabs in a text become spaces; control characters and invisible text-direction overrides are
  removed. A text that ends up empty is dropped.
