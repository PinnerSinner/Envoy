# Security

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository instead.

## What Envoy does with your data

- **API keys** are stored in `%APPDATA%\Envoy\settings.json`, encrypted with Windows DPAPI (current-user scope). They
  are sent only to the provider they belong to, over HTTPS.
- **Chat text** is sent only to the translation provider you choose. Nothing is sent anywhere else.
- **Update checks** (optional) make one unauthenticated request to `api.github.com` on start-up.
- **Transcripts** (optional, off by default) are plain-text files in `%APPDATA%\Envoy\logs`.
- Envoy never runs elevated, never reads other processes' memory, and only sends keystrokes when you press Send in
  the reply box.
