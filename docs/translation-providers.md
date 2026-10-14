# Translation providers

Envoy has **no server of its own**. Each copy talks directly to the provider you choose, using your own account if
the provider needs one. Two things follow:

- You can share `Envoy.exe` freely. Nobody who downloads it can use your keys or credits. Keys are stored on your
  PC only, encrypted with Windows DPAPI.
- Chat text goes only to the provider you pick. Envoy collects nothing.

## Choosing

| Provider | Cost | Quality | Setup |
| --- | --- | --- | --- |
| Google Translate (free) | Free | Good | Nothing. This is the default. |
| Microsoft Translator (free) | Free | Good | Nothing. This is the default fallback. |
| DeepL | Free up to 500k characters a month | Very good (especially European languages) | API key |
| Claude (Anthropic) | Pay-as-you-go, from fractions of a cent per message | Best with slang, abbreviations and tone | API key |
| Amazon Translate | $15 per million characters (free tier and credits apply) | Good | AWS access key |
| LibreTranslate | Free (self-hosted) | Fair | Server URL |

**Recommendation:** start with the free default. If you want better handling of banter ("你是不是在搞笑", "ㅋㅋ 개못하네"),
add DeepL's free plan or Claude.

A busy match produces about 2,000–5,000 characters of chat. Repeated messages are cached, so they're only
translated once. The cache lasts until you close Envoy, and survives settings changes; turn on
[Remembering translations](#remembering-translations) to keep it between sessions.

## Fallback chain

**Translation → Providers** has a main provider and an *if it fails, try* provider. Google Translate (free) is always
added as the last resort. A missing key, a spent budget or an outage therefore never leaves you without
translations. The line under the provider picker (and the War Room) says which provider is translating right now, and
the **Provider health** card shows each one's state.

## When a provider fails

Envoy gives each chat line a time limit, **Give up after** (15 seconds by default, under **Translation → Behaviour**).
Within it, Envoy:

- **retries** a provider once or twice after a brief hiccup (a server error, a timeout, "too many requests"). If the
  provider says to wait a second or two, Envoy waits; if it says to wait longer, Envoy moves on at once;
- **moves on** to the next provider as soon as one fails, times out, or rejects your key, so one slow provider never
  holds up your chat;
- **skips** a provider that keeps failing for a while (20 seconds at first, longer if it keeps failing, at most
  5 minutes), then tries it again with the next message. A rejected key is skipped until you change it or press
  **Retry now**; a used-up quota until the provider says it resets (or an hour), and your own daily cap until midnight;
- **paces** requests to the free providers (a short burst, then about two a second), because a flood of requests is
  the usual way an IP address gets blocked by them. The reply box has its own, slower allowance so typing a reply never
  slows down chat translation.

If every provider fails, the overlay shows why, for example "Translation unavailable. Google Translate (free) is
cooling down after repeated failures." Envoy never shows a provider's raw error page.

The **Provider health** card lists the providers in the order Envoy tries them, each with a coloured dot, what it is
doing ("Working · 340 ms", "Skipping for 18 s after repeated failures.", "Your key was rejected. Check it above.") and
when it last translated. **Retry now** forgets the failures and tries that provider again with the next message.
Characters are counted against your daily cap only when a request may have reached a paid provider: a request that
was skipped, or never left your PC, is given back.

## Daily spending cap

Paid providers (DeepL, Claude, Amazon) have a daily character cap, 50,000 by default. When a provider hits it,
Envoy skips it until local midnight and uses the next one in the chain. Today's usage is shown in the War Room.
Set the cap to 0 to remove it.

## Free providers

The two free providers use the public endpoints that Google's website translator widget and Microsoft Edge's
built-in page translation use. They need no account, but they are undocumented. They can rate-limit very heavy use
or change without notice. If one stops working, the other takes over. If both do, add a key-based provider.

## Remembering translations

**Translation → Saved translations → Remember translations between sessions** is off by default. When it is on,
Envoy saves its translation cache to `translation-cache.json` in its data folder, at most every 30 seconds and when it
closes, so "gg wp" next match is instant (and free with a paid provider). The file holds translations of chat lines
you received, in your language, plus a one-way fingerprint of each original line. Reply-box translations (what you
type and its back-translation) are never saved. It never leaves this PC. The
fingerprint stops casual reading of the original lines, but someone with the file could guess short common ones, so
treat it like a chat transcript.

It keeps at most 2,000 entries and 1 MiB, forgets entries after 30 days, and never stores lines longer than 200
characters. **Clear saved translations**, or turning the option off, deletes the file. A damaged file is set aside as
`translation-cache.json.bad` and Envoy starts with an empty cache. See [Privacy](privacy.md).

## DeepL

1. Create a **DeepL API Free** account at <https://www.deepl.com/pro-api>. A card is needed for identity checks but
   isn't charged on the free plan.
2. Copy the authentication key (free keys end in `:fx`).
3. In Envoy, go to **Translation**, choose **DeepL** as the main or fallback provider, and paste the key.

## Claude

1. Create an API key at <https://console.anthropic.com/> and add a little credit.
2. In Envoy, choose **Claude**, paste the key and pick a model:
   - **claude-haiku-5-5**: very cheap and fast, and plenty for chat. Recommended.
   - **claude-sonnet-5-5**: a middle ground.
   - **claude-opus-5-5**: the most capable and the most expensive. This is the default.
3. Consider setting a monthly spend limit in the Anthropic console as well as Envoy's daily cap.

Envoy asks Claude to translate the way a fellow player would talk, and to keep RTS terms (boom, FC, TC, vills) as
players actually use them.

## Amazon Translate (AWS credits)

1. In the AWS console, create an **IAM user** for Envoy with only this policy:
   ```json
   {
     "Version": "2012-10-17",
     "Statement": [{ "Effect": "Allow", "Action": "translate:TranslateText", "Resource": "*" }]
   }
   ```
2. Create an access key for that user.
3. In Envoy, choose **Amazon Translate** and enter the access key ID, the secret and a region (e.g. `us-east-1`).
4. Set up an AWS Budgets alert, so a surprise can't happen even if something goes wrong.

Never put root-account keys in any app.

## LibreTranslate (fully self-hosted)

Run LibreTranslate yourself (e.g. `docker run -p 5000:5000 libretranslate/libretranslate`), then choose
**LibreTranslate** in Envoy and set the URL (default `http://localhost:5000`). Translation then never leaves your
PC. Quality is below the commercial services.

## Slang glossary

Before any provider is called, Envoy checks whether the whole message is common gaming shorthand: `666`, `2333`,
`88`, `牛逼`, `ㅋㅋㅋ`, `ㅈㅈ`, `ㄱㄱ`, `изи`, `гг`, `jajaja`, `kkkk`, `www` and about 50 more. These are explained instantly,
offline and for free. The list lives in `src/Envoy.Core/Translation/SlangGlossary.cs`; additions are welcome.
