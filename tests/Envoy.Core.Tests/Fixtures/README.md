# Provider fixtures

`Providers/<provider>/<scenario>.json` are recorded-shape HTTP responses for `ProviderContractTests`
(spec 03, REQ-TRR-24). Each file is `{ "status", "headers", "contentType", "body" }`.

Provenance: written by hand from each provider's documented response format and the shapes the parse tests already
pinned (spec 03, Risk 4). No real keys, account ids or chat lines appear. Replace them with real captures (keys, ids
and chat removed) when one is taken, and note it here.

| Provider | Scenarios |
| --- | --- |
| `google-free` | `success`, `429`, `500`, `malformed` (HTML captcha page), `403` (the keyless endpoint refusing us) |
| `edge-free` | `success`, `429`, `500`, `malformed`, `401`; `auth-token.txt` is the token the auth endpoint returns |
| `deepl` | `success`, `429`, `500`, `malformed`, `auth` (403), `456` (quota) |
| `libretranslate` | `success`, `429`, `500`, `malformed`, `auth` (403), `400` (unsupported language) |
| `claude` | `success` (a complete Messages API object), `429`, `500`, `malformed`, `auth` (401) |
| `aws` | `success`, `429` (ThrottlingException), `500`, `malformed`, `auth` (UnrecognizedClientException), `quota` (LimitExceededException) |
