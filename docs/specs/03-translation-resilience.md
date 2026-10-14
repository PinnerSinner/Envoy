# Translation resilience: retries, circuit breakers, throttling, health and a lasting cache

Status: Proposed — revised against `00-integration.md` (binding). Lands as step 4, after 01, 08a and 02.

Area id: `03-translation-resilience` · Requirement prefix: `TRR` · Code lives in `src/Envoy.Core/Translation/` and
`tests/Envoy.Core.Tests/Translation/`. The app changes are thin: composition in `AppHost`, one health card plus a
cache toggle on `Pages/TranslationPage.xaml`, and two smoke sections. `ITranslator` keeps its current signature.

This area lands as **two CI-green PRs** (contract §1, step 4):

- **PR A — core and wiring.** Error kinds, `ProviderHttp`, retry, breaker, throttle, `TranslationRuntime`,
  `ProviderHealthRegistry`, the memory cache in the runtime, the fallback timeout fix, budget refunds, SDK retries off,
  contract tests, `AppHost` composition, the health-derived `ProviderStatus`, logging, and the diagnostics section.
  PR A alone fixes the user-visible hang.
- **PR B — persistence and UI.** The opt-in disk cache, the Translation page health card and toggles, the
  `DataInventory`/`docs/privacy.md` entries for the cache file, and the smoke sections for health.

## Context

### What exists today

- **Interface.** `src/Envoy.Core/Translation/ITranslator.cs` defines `ITranslator { Id; IsMetered; TranslateAsync(TranslationRequest, CancellationToken) }`,
  the records `TranslationRequest` and `TranslationResult`, `TranslationException(string message, Exception? inner)`,
  and `BudgetExceededException`. Exceptions carry no error category, so a caller cannot tell "key rejected" from
  "server hiccup".
- **The stack** (`TranslatorFactory.Build`) is `Caching( Fallback[ Budgeted(primary), Budgeted(fallback…), GoogleFree ] )`.
  `AppHost.ApplySettings()` calls `BuildTranslator()` after **every** debounced settings edit. Each rebuild creates a
  new `CachingTranslator`, so every settings edit empties the in-memory cache. `Build` also reports providers it could
  not construct (for example a missing key) through `onProviderFailed`.
- **Providers** (`Providers/*.cs`):
  - `GoogleFreeTranslator`, `EdgeFreeTranslator`, `DeepLTranslator` and `LibreTranslateTranslator` make one call on
    the shared `HttpClient` that `AppHost` creates with `Timeout = 12 s`. A non-2xx status becomes a plain
    `TranslationException`. `Retry-After` is ignored. DeepL special-cases 403 and 456. `EdgeFreeTranslator` clears its
    token on 401 but does not retry.
  - Each `Parse` calls `JsonDocument.Parse` and `GetProperty`/indexers directly. A malformed body (for example an HTML
    captcha page from the keyless Google endpoint) escapes as `JsonException`, `KeyNotFoundException` or
    `IndexOutOfRangeException`, and the overlay shows text like `'<' is an invalid start of a value`.
  - `ClaudeTranslator` builds its own `AnthropicClient` with SDK defaults (internal retries, SDK timeout, its own
    `HttpClient`). `AwsTranslator` uses `AmazonTranslateClient` with SDK-default retries. Neither can be pointed at a
    fake HTTP handler today.
- **`FallbackTranslator.cs`** catches `Exception ex when ex is not OperationCanceledException`. **Bug:** when
  `HttpClient.Timeout` expires, .NET throws `TaskCanceledException`, an `OperationCanceledException`. The timeout is
  rethrown and **not** passed to the next provider. `ChatPipeline.TranslateAsync` then reaches
  `catch (OperationCanceledException) { return; }` and never raises `MessageUpdated`. A slow Google endpoint leaves the
  overlay line on "translating…" for good, and Edge is never tried.
- **`CachingTranslator.cs`** is a 500-entry in-memory LRU keyed by `source␟target␟trimmed text`. The key omits the
  provider, so after switching to Claude the user is still served cached Google output. Nothing survives a restart.
- **`UsageTracker.cs` / `BudgetedTranslator`** reserve characters **before** the call, so characters are counted even
  when the call never leaves the PC.
- **Concurrency.** `ChatPipeline.ProcessAsync` translates a burst with `Task.WhenAll`, so 8 new lines send 8
  simultaneous requests to the keyless Google endpoint. `ComposeWindow.TranslateNowAsync` translates and
  back-translates on each typing pause through `Host.Outgoing` and `Host.Translator`, on the same chain.
- **Health.** `AppHost.BuildTranslator` turns `FallbackTranslator.ProviderFailed` into one string,
  `AppHost.ProviderStatus`: the last error only, with raw exception text.
- **Tests.** `tests/Envoy.Core.Tests/TranslationTests.cs` has parse tests for Google, Edge, DeepL and Claude and stack
  tests using a `FakeTranslator`. No test covers HTTP status handling, headers, request shape, LibreTranslate parsing
  or AWS.

### What earlier steps deliver (contract §1)

- **01:** `Pages/TranslationPage.xaml`, `ViewModels/MainViewModel.Translation.cs`, `PageIds`, `Envoy.Core.Mvvm`
  commands, the three-phase smoke test over `ThemeManager.PaletteIds × PageIds.All`, and the `IsVisibleChanged` →
  view-model flag pattern for page-scoped timers.
- **08a:** analyzers with warnings as errors under `CI=true`, `BannedSymbols.txt`, `tools/ci/policy.json`
  (`network.allowedPaths` includes `src/Envoy.Core/Translation/` and `src/Envoy.App/AppHost.cs`), and `[SuppressMessage("CA1001")]`
  on `ClaudeTranslator`/`EdgeFreeTranslator` that names this area as the disposal owner.
- **02:** `ILog`/`LogHub`/`LogRedactor` (mask `[redacted]`), `AppHostOptions` (incl. `TimeProvider`), instance
  `AppHost.DataFolder`, `AppHost.Log`, `AppHost.Time`, `SaveSuppressed`/`SuppressSave()`, `DataInventory`,
  `docs/privacy.md` ("What Envoy stores on your PC"), `DiagnosticsSnapshot`/`DiagnosticsReport` with a Translation
  section waiting for this area, and the `CLAUDE.md` rules "every `catch` in `AppHost.cs` logs" and "Dispose-time
  writers check `SaveSuppressed`".

### What is weak or missing for production

1. A timeout on the primary provider can hang a message forever, and the fallback is skipped.
2. No retries for blips (one 502 means one failed line) and no `Retry-After` handling.
3. A dead or blocked keyless endpoint is retried in full on every chat line; each line waits up to 12 s.
4. Nothing protects the unofficial keyless endpoints from bursts, the usual way an IP gets blocked.
5. SDK retries inside Claude and AWS multiply with any outer retry, and their timeouts are not ours.
6. The UI cannot answer "is translation working, and which provider is in use?".
7. The cache dies on every settings edit and at exit, so paid users pay again for "gg wp" every match.
8. No tests pin the HTTP contract of each provider.

## Goals / Non-goals

**Goals**

- Every translation call ends within a bounded time, with a result or a categorised `TranslationException`. It never
  hangs, and a timeout is never silently dropped.
- Retry transient failures cheaply and move to the next provider quickly when one is down, throttled or rejecting
  the key.
- Keep the keyless endpoints happy with a per-provider token bucket.
- Be the **single owner of provider health** (contract §2.1): one registry that the Translation page, the War Room
  status line, 02's diagnostics report and 07a's `ProviderFailing` notice all read.
- A memory cache that survives settings edits and never mixes providers; an opt-in, size-capped disk cache (SHOULD).
- Contract tests for all six providers against recorded HTTP responses.

**Non-goals**

- Changing `ITranslator`, `TranslationRequest`, `TranslationResult`, or `ChatPipeline`. The pending-message lifecycle
  on caller cancellation (watcher stop, `NewMatch`, dispose) belongs to 04 (contract §2.6). This area only guarantees
  that a timeout surfaces as `TranslationException(Kind = Timeout)` and that caller cancellation surfaces as
  `OperationCanceledException`.
- Adding or removing providers, changing prompts, or batching lines into one request.
- Logging infrastructure (02) and notice policy (07a). This area raises events; they subscribe.
- Adding Polly or `System.Threading.RateLimiting`. The pieces are small and need `TimeProvider` for tests.
- New P/Invoke, new network code paths outside `src/Envoy.Core/Translation/` and `AppHost.cs`, or new hosts (§10).

## Requirements

16 MUST (contract §9 budget: TRR-1, 2, 4, 6, 7, 8, 10, 11, 12, 16, 17, 18, 19, 22, 23, 24). Ids keep their original
numbers; folded ids stay in the table so references do not break.

| ID | Pri | PR | Requirement |
| --- | --- | --- | --- |
| REQ-TRR-1 | MUST | A | `TranslationException` carries a `Kind` (`TranslationErrorKind`), an optional HTTP `StatusCode` and an optional `RetryAfter`. Every provider throws only `TranslationException` (or caller-requested `OperationCanceledException`) for HTTP errors, network errors, timeouts and malformed bodies. Messages are whole sentences from `TranslationErrorText` (contract §8.7): provider name plus what happened, never a response body, raw exception text or "Error:" prefix. |
| REQ-TRR-2 | MUST | A | HTTP statuses are classified consistently: 408/500/502/503/504 (and Anthropic 529) → `Transient`; 429 → `RateLimited`; 401/403 on keyed providers → `Auth`; DeepL 456 and AWS `LimitExceededException` → `QuotaExceeded`; 400/404/413/414/422 → `Unsupported`; an unparseable 2xx body → `BadResponse`. `Retry-After` is parsed as delta-seconds and as an HTTP-date and clamped to 0–3600 s (absorbs REQ-TRR-3). |
| REQ-TRR-3 | — | | *Folded into REQ-TRR-2.* |
| REQ-TRR-4 | MUST | A | `Transient`, `Timeout` and `RateLimited` failures are retried up to the provider's `MaxAttempts` with exponential backoff and full jitter (`delay = U(0,1) × min(MaxDelay, BaseDelay × 2^(attempt−1))`), never past the overall deadline. `Auth`, `QuotaExceeded`, `BadResponse`, `Unsupported`, `Configuration` and `Unavailable` are never retried. |
| REQ-TRR-5 | SHOULD | A | When a 429/503 carries `Retry-After` ≤ `MaxRetryAfterWait` that fits the remaining deadline, the retry waits that long instead of the backoff. A longer `Retry-After` is not waited for: the provider's breaker opens as `Throttled` until then and the request moves to the next provider. |
| REQ-TRR-6 | MUST | A | Each attempt has its own timeout (`AttemptTimeout`). Each top-level call has an overall deadline (`TranslationSettings.RequestTimeoutSeconds`, default 15 s, clamped 3–60) shared across retries and fallback providers, read from the immutable `TranslationRuntimeOptions` snapshot (contract §7). Hitting either produces `TranslationException(Kind = Timeout)`, not `OperationCanceledException`. |
| REQ-TRR-7 | MUST | A | `FallbackTranslator` moves to the next provider when a provider times out (including `TaskCanceledException` from `HttpClient.Timeout`), and rethrows `OperationCanceledException` only when the caller's token was cancelled. A message whose every provider times out ends `Failed`, not `Pending`. |
| REQ-TRR-8 | MUST | A | Each provider has a circuit breaker. After `BreakerThreshold` consecutive counted failures it opens for `BreakerOpenDuration`; while open, calls fail in under 1 ms with `ProviderUnavailableException` and no network I/O. After the open period exactly one half-open probe is let through and concurrent calls are rejected; success closes the breaker and resets the backoff; failure reopens it with the duration doubled up to `BreakerMaxOpenDuration` (absorbs REQ-TRR-9). |
| REQ-TRR-9 | — | | *Folded into REQ-TRR-8.* |
| REQ-TRR-10 | MUST | A | An `Auth` failure opens the breaker at once as `AuthFailed` until the provider's configuration fingerprint changes or "Retry now" is pressed. A `QuotaExceeded` failure opens it until `RetryAfter`, else 1 hour (`BudgetExceededException`: until next local midnight). `Unsupported`, `Configuration` and caller cancellations do not count towards the breaker. |
| REQ-TRR-11 | MUST | A | Breaker, throttle and health state for a provider live in the long-lived `TranslationRuntime` and survive `TranslatorFactory.Build` rebuilds. They reset only when that provider's configuration fingerprint (key, URL, model, region) changes or `ResetProvider` ("Retry now") is called. |
| REQ-TRR-12 | MUST | A | Each keyless provider (`google-free`, `edge-free`) has a token-bucket throttle (defaults in Design §4). A request waits at most `MaxQueueWait` for a token; past that it fails with `ProviderUnavailableException(Throttled)` without a network call and moves to the next provider. |
| REQ-TRR-13 | SHOULD | A | Each provider has a concurrency limit (`MaxConcurrency`) and a waiting-queue limit (`MaxQueued`); a request beyond the queue limit fails fast with `ProviderUnavailableException`. |
| REQ-TRR-14 | SHOULD | A | Concurrent requests with the same cache key share a single underlying call and all receive its result or exception. |
| REQ-TRR-15 | SHOULD | A | When a coalesced waiter cancels, only its own wait ends; the shared call is cancelled when every waiter has cancelled. A request cancelled while waiting for a slot, a token or a backoff delay makes no network call. |
| REQ-TRR-16 | MUST | A | `ProviderHealthRegistry` (exposed as `TranslationRuntime.Health` and `AppHost.TranslationHealth`) is the single provider-health source. Per provider it reports the contract §2.1 `ProviderHealthSnapshot`: `State` (`Unknown, Healthy, Degraded, Throttled, CoolingDown, AuthFailed, QuotaExceeded`), last success/failure times, sanitised last error and kind, consecutive failures, median latency of the last 20 successes, counts and `RetryAt`. `TranslationRuntime.ProviderHealthChanged(ProviderHealthSnapshot)` is raised on **every** state transition, including one caused by a locally rejected (breaker-skipped or throttled) call, and on no other change. `AppHost` re-raises it on the UI thread, logs each transition at Info under category `Translation`, and 02's diagnostics report lists the snapshot for the active chain. |
| REQ-TRR-17 | MUST | A | Health errors, diagnostic events, log fields and the `FallbackTranslator` aggregate message never contain chat text, API keys or response bodies. They are built from provider name, kind, status code and fixed `TranslationErrorText` sentences, cut to 160 characters, then passed through 02's `LogRedactor.Redact` (mask `[redacted]`). `ProviderHealthText.Sanitize` delegates to that redactor and has no masking logic of its own. |
| REQ-TRR-18 | MUST | B | The Translation page shows one health row per provider in the active chain (`MainViewModel.ProviderHealthRows`: name, state dot from palette keys only, state text, median latency, last success) with a "Retry now" button per row. Every new control has `AutomationProperties.Name`. `AppHost.ProviderStatus` (War Room line) is derived from the registry via `ProviderHealthText.Summary`. *(The `ProviderStatus` derivation lands in PR A.)* |
| REQ-TRR-19 | MUST | A | The memory cache is owned by `TranslationRuntime`, survives settings edits, and keys entries by a namespace of the primary provider id (plus the model when Claude is primary). Switching the primary provider never serves the old provider's output. |
| REQ-TRR-20 | SHOULD | B | When `TranslationSettings.RememberTranslations` is on (default **off**), the cache is saved to `AppHost.TranslationCachePath` (`{DataFolder}/translation-cache.json`). Saves are atomic (temp file + replace), run at most every 30 s while dirty and at `Host.Dispose`, and never run after `TranslationRuntime.SuppressPersistence()` (called by `AppHost.SuppressSave()`). Size is capped by `CacheMaxEntries` (default 2000) and 1 MiB, dropping least-recently-used entries; entries expire after 30 days; requests longer than 200 characters are not saved. The file and its `.tmp`/`.bad` siblings join `DataInventory.Items` and `docs/privacy.md` in the same PR. |
| REQ-TRR-21 | SHOULD | B | Turning `RememberTranslations` off, or pressing "Clear saved translations", deletes the cache file and clears memory. A corrupt or unreadable file is renamed to `.bad` (logged at Warning) and the app starts with an empty cache. |
| REQ-TRR-22 | MUST | A | `BudgetedTranslator` gives back reserved characters (`UsageTracker.Refund`) when the call never left the PC: `Unavailable` (local rejection), `Configuration`, or a connection/name-resolution failure before the request was sent. Timeouts and HTTP errors are **not** refunded (the provider may have billed them). Retries of one logical call reserve characters once. |
| REQ-TRR-23 | MUST | A | The Claude and AWS SDK clients have internal retries off (`MaxRetries = 0`, `MaxErrorRetry = 0`) and use the per-attempt timeout. Claude uses the injected shared `HttpClient`. |
| REQ-TRR-24 | MUST | A | Every provider has contract tests driven by a fake `HttpMessageHandler` and recorded fixtures for success, 429 (with `Retry-After`), 500, malformed body and auth error (or the closest keyless equivalent). They check the request shape (method, host/path, auth header, key fields) and the mapped result or `Kind`. For AWS, if a fake handler proves fragile (Risk 3), mapping tests over constructed `AmazonServiceException`s plus a config assertion satisfy this. |
| REQ-TRR-25 | SHOULD | A | `EdgeFreeTranslator` gets a fresh token and retries once, inside the same attempt, when the translate call returns 401. A second 401 is `RateLimited` (keyless endpoint refusing us), not `Auth`. |
| REQ-TRR-26 | SHOULD | A | `TranslationRuntime` raises `Diagnostic` (`TranslationDiagnostic`: provider id, outcome, kind, status, attempt, elapsed) for every attempt. `AppHost` logs it at Info under `Translation` (contract §2.1), replacing 02's interim `onProviderFailed` Warning. |
| REQ-TRR-27 | COULD | A | When every provider in the chain is rejected locally, `FallbackTranslator` makes one real attempt on the provider with the earliest `RetryAt` that is not `AuthFailed`/`QuotaExceeded`. |
| REQ-TRR-28 | SHOULD | B | `RequestTimeoutSeconds` is editable on the Translation page ("Give up after", 5–30 s slider). |
| REQ-TRR-29 | SHOULD | A | Compose (reply box) requests use a separate low-priority lane (contract §8.8): `AppHost.ComposeTranslator`, built with `TranslationLane.Compose`, draws from its own small token bucket and a concurrency of 1 per provider, shares the breaker, health and memory cache with chat, and never consumes the chat lane's tokens or slots. |

## Design

### 1. Error model (`ITranslator.cs`, changed; `TranslationErrors.cs`, `TranslationErrorText.cs`, new)

```csharp
namespace Envoy.Core.Translation;

public enum TranslationErrorKind
{
    Unknown,        // not classified; counted by the breaker, not retried
    Transient,      // 408, 5xx, 529, network failure (HttpRequestException, IOException)
    Timeout,        // attempt timeout or overall deadline
    RateLimited,    // 429, or a keyless endpoint refusing us (403 on Google, second 401 on Edge)
    Auth,           // key rejected
    QuotaExceeded,  // provider quota (DeepL 456) or Envoy's daily cap
    BadResponse,    // 2xx with a body we cannot parse
    Unsupported,    // request-specific 4xx (language pair, size); not counted by the breaker
    Configuration,  // missing key / URL; not counted by the breaker
    Unavailable,    // rejected locally: breaker open, throttled, queue full
}

// Optional parameters only: existing `new TranslationException(msg)` / `(msg, inner)` calls compile unchanged.
public class TranslationException(
    string message, Exception? inner = null,
    TranslationErrorKind kind = TranslationErrorKind.Unknown,
    int? statusCode = null, TimeSpan? retryAfter = null) : Exception(message, inner)
{
    public TranslationErrorKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class BudgetExceededException(string providerId, int limit)
    : TranslationException(/* text unchanged */, kind: TranslationErrorKind.QuotaExceeded) { … }

public sealed class ProviderUnavailableException(string providerId, ProviderState state, DateTimeOffset? retryAt, string message)
    : TranslationException(message, kind: TranslationErrorKind.Unavailable)
{
    public string ProviderId { get; } = providerId;
    public ProviderState State { get; } = state;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
```

**`TranslationErrorText`** (Core, contract §8.7) holds whole-sentence format strings with named placeholders, for
example `RateLimited = "{provider} is limiting requests right now."`, `BadResponse = "{provider} sent a reply Envoy
could not read."`, `AuthRejected = "{provider} rejected your key. Check it on the Translation page."`,
`AllFailed = "Translation unavailable: {providers}."`, `Timeout = "No translation within {seconds} s."`. A
`Format(string template, params (string Name, string Value)[] args)` helper fills them using
`CultureInfo.CurrentCulture` (CA1305). Missing-credential paths pass `kind: Configuration`.

### 2. Shared HTTP helper (`Providers/ProviderHttp.cs`, new, `internal static`)

```csharp
internal static class ProviderHttp
{
    /// Throws a classified TranslationException for non-2xx responses (per-provider overrides), else returns the body.
    public static Task<string> ReadBodyAsync(HttpResponseMessage response, string providerName, TimeProvider clock,
        Func<int, TranslationErrorKind?>? overrideKind, CancellationToken ct);
    public static TranslationErrorKind Classify(int status);                                   // REQ-TRR-2 table
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset now); // REQ-TRR-2
    /// Runs a parser; JsonException/KeyNotFound/IndexOutOfRange/InvalidOperation/FormatException → BadResponse.
    public static TranslationResult Parse(string providerName, string body, Func<string, TranslationResult> parse);
}
```

Request URLs and bodies stay byte-for-byte the same. Every `Parse` call goes through `ProviderHttp.Parse`.

| Provider | Changes |
| --- | --- |
| `GoogleFreeTranslator` | `ReadBodyAsync` with override 403 → `RateLimited`. A 2xx body that is not a JSON array → `BadResponse`. |
| `EdgeFreeTranslator` | Override 401 → refresh the token and resend once (REQ-TRR-25, SHOULD); a second 401 → `RateLimited`. A failing auth endpoint → `Transient`. Constructor unchanged. |
| `DeepLTranslator` | 403 → `Auth`, 456 → `QuotaExceeded`. |
| `LibreTranslateTranslator` | `Parse` becomes `internal static Parse(string json, string providerId)`. 403 → `Auth`, 400 → `Unsupported`. |
| `ClaudeTranslator` | New constructor `ClaudeTranslator(string apiKey, string model, HttpClient? http = null, TimeSpan? timeout = null)`, setting `AnthropicClient { HttpClient = http, MaxRetries = 0, Timeout = timeout ?? 12 s }`. Mapping: `AnthropicRateLimitException` → `RateLimited`; `AnthropicUnauthorizedException`/`AnthropicForbiddenException` → `Auth`; `Anthropic5xxException`, status 529 and `AnthropicIOException` → `Transient`; `AnthropicBadRequestException`/`AnthropicUnprocessableEntityException` → `Unsupported`; `AnthropicInvalidDataException` → `BadResponse`. `RetryAfter` only if the SDK exposes response headers (Risk 2). The refusal path stays `Unsupported`. |
| `AwsTranslator` | Optional `Amazon.Runtime.HttpClientFactory? httpClientFactory = null` (tests only) and `TimeSpan? timeout`; `AmazonTranslateConfig { MaxErrorRetry = 0, Timeout = …, HttpClientFactory = … }`. Mapping by `ErrorCode`: throttling codes → `RateLimited`; `UnrecognizedClientException`/`InvalidSignatureException`/`AccessDeniedException`/`ExpiredTokenException` → `Auth`; `LimitExceededException` → `QuotaExceeded`; `UnsupportedLanguagePairException`/`TextSizeLimitExceededException`/`DetectedLanguageLowConfidenceException` → `Unsupported`; `ServiceUnavailableException`/`InternalServerException`/5xx/`AmazonClientException` → `Transient`. |

`TranslatorFactory.Create` passes the shared `HttpClient` to Claude and the per-provider `AttemptTimeout` to Claude and
AWS. Claude no longer owns an `HttpClient`; 08a's CA1001 suppressions on Claude/Edge stay with the justification
updated to "translators are rebuilt on settings edits and own no handle that needs disposal: the `HttpClient` is
AppHost's, and `SemaphoreSlim` allocates no wait handle unless `AvailableWaitHandle` is read".

### 3. Resilience building blocks (`src/Envoy.Core/Translation/Resilience/`, new)

All take a `TimeProvider` (production: `AppHost.Time`; tests: `FakeTimeProvider`). Waits use
`Task.Delay(TimeSpan, TimeProvider, CancellationToken)` and `new CancellationTokenSource(TimeSpan, TimeProvider)`.

```csharp
public sealed record ResilienceOptions
{
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public int MaxAttempts { get; init; } = 2;                       // 1 = no retry
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxRetryAfterWait { get; init; } = TimeSpan.FromSeconds(2);
    public int BreakerThreshold { get; init; } = 3;
    public TimeSpan BreakerOpenDuration { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BreakerMaxOpenDuration { get; init; } = TimeSpan.FromMinutes(5);
    public double TokensPerSecond { get; init; } = 0;                // 0 = no throttle
    public int Burst { get; init; } = 0;
    public TimeSpan MaxQueueWait { get; init; } = TimeSpan.FromMilliseconds(1500);
    public int MaxConcurrency { get; init; } = 4;                    // SHOULD (REQ-TRR-13)
    public int MaxQueued { get; init; } = 16;
    public static ResilienceOptions For(string providerId, TranslationLane lane = TranslationLane.Chat);  // §4
}

public enum TranslationLane { Chat, Compose }

public static class RetryPolicy
{
    public static bool IsRetryable(TranslationErrorKind kind);       // Transient, Timeout, RateLimited
    public static TimeSpan Backoff(int attempt, ResilienceOptions o, double jitter01);   // 1-based, full jitter
    /// Delay before the next attempt, or null when the caller should stop and fall through.
    public static TimeSpan? NextDelay(TranslationException error, int attempt, ResilienceOptions o,
                                      TimeSpan? remainingDeadline, double jitter01);
}

public enum BreakerState { Closed, Open, HalfOpen }

public sealed class CircuitBreaker(ResilienceOptions options, TimeProvider clock)
{
    public BreakerState State { get; }
    public DateTimeOffset? OpenUntil { get; }
    public ProviderState OpenReason { get; }          // CoolingDown / Throttled / AuthFailed / QuotaExceeded
    public bool TryEnter(out bool isProbe);           // false → reject fast
    public void OnSuccess();
    public void OnFailure(TranslationException error); // REQ-TRR-8/10 rules
    public void OnProbeCancelled();                   // back to Open with the same OpenUntil; not a failure
    public void Reset();
}

public sealed class TokenBucket(double tokensPerSecond, int burst, TimeProvider clock)
{
    public bool TryTake();
    public Task<bool> TakeAsync(TimeSpan maxWait, CancellationToken ct);   // false if no token in time
}
```

**`ProviderGuard`** (internal) holds one provider's state: breaker, one `TokenBucket` and `SemaphoreSlim` gate **per
lane**, queued counts, the per-provider `ProviderHealthEntry` (internal), options and configuration fingerprint. It
lives in `TranslationRuntime`, keyed by provider id.

**`ResilientTranslator(ITranslator inner, ProviderGuard guard, TranslationLane lane, TimeProvider clock, Func<double>? jitter = null) : ITranslator`**
handles one logical call:

1. `Breaker.TryEnter` fails → `ProviderUnavailableException` (state from `OpenReason`, `RetryAt = OpenUntil`).
2. Lane queue full → `ProviderUnavailableException` (SHOULD, REQ-TRR-13). Otherwise wait on the lane gate, bounded by
   the remaining deadline and the caller's token.
3. Lane `TokenBucket.TakeAsync(MaxQueueWait)` returns false → `ProviderUnavailableException(Throttled)`; health
   records a local rejection (state `Throttled` until the next real outcome).
4. Attempt loop: a linked CTS of the caller token plus `min(AttemptTimeout, remaining deadline)`. An
   `OperationCanceledException` while the caller's token is **not** cancelled → `TranslationException(Timeout)`.
   `HttpRequestException`/`IOException` → `Transient` (with `HttpRequestException.HttpRequestError` kept so
   `BudgetedTranslator` can tell a pre-send connection failure). Any other non-`TranslationException` → `Unknown`.
   `RetryPolicy.NextDelay` decides between waiting and rethrowing.
5. Records the outcome in breaker and health, raises `Diagnostic`, releases the gate in `finally`. A cancelled
   half-open probe calls `OnProbeCancelled`.

**`TranslationDeadline`** (internal, `AsyncLocal<DateTimeOffset?>`). `FallbackTranslator` sets it for the length of a
call so `ResilientTranslator` knows the time left without changing `ITranslator`.

**`CoalescingTranslator(ITranslator inner, Func<TranslationRequest, string> key) : ITranslator`** (SHOULD,
REQ-TRR-14/15) keeps a lock-protected `Dictionary<string, Flight>`. A `Flight` holds a shared task, its own CTS
(disposed when the flight completes) and a waiter count. Each caller awaits `flight.Task.WaitAsync(callerToken)`;
at zero waiters the flight's CTS is cancelled. Failures are shared.

### 4. Default policy per provider (`ResilienceOptions.For`)

Conservative client-side defaults, **not** the providers' published limits (unknown for the keyless endpoints). They
are code constants, not settings, so they can be tuned after release without a settings change.

| Provider | AttemptTimeout | MaxAttempts | Chat throttle (tokens/s, burst) | MaxConcurrency | BreakerThreshold / open |
| --- | --- | --- | --- | --- | --- |
| `google-free` | 5 s | 2 | 2/s, 6 | 3 | 3 / 20 s → 5 min |
| `edge-free` | 5 s | 2 | 2/s, 6 | 3 | 3 / 20 s → 5 min |
| `deepl` | 6 s | 3 | none | 4 | 5 / 20 s → 5 min |
| `claude` | 12 s | 2 | none | 2 | 3 / 20 s → 5 min |
| `aws` | 6 s | 3 | none | 4 | 5 / 20 s → 5 min |
| `libretranslate` | 8 s | 2 | none | 2 | 3 / 10 s → 2 min |

**Compose lane** (SHOULD, REQ-TRR-29): keyless providers 0.5 tokens/s, burst 2; every provider `MaxConcurrency = 1`,
`MaxQueued = 1`, `MaxAttempts = 1` (the reply box retries naturally on the next typing pause). The worst-case rate to
a keyless endpoint is therefore 2.5 requests/s.

### 5. Health (`Resilience/ProviderHealthRegistry.cs`, `ProviderHealthText.cs`, new)

Names follow contract §2.1. No type or member in this area is called `ProviderHealth`.

```csharp
public enum ProviderState { Unknown, Healthy, Degraded, Throttled, CoolingDown, AuthFailed, QuotaExceeded }

public sealed record ProviderHealthSnapshot(
    string ProviderId, ProviderState State,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure,
    string? LastError, TranslationErrorKind? LastErrorKind,
    int ConsecutiveFailures, TimeSpan? MedianLatency,
    DateTimeOffset? RetryAt, long Successes, long Failures);

public sealed class ProviderHealthRegistry
{
    public ProviderHealthSnapshot Get(string providerId);
    public IReadOnlyList<ProviderHealthSnapshot> Snapshot(IEnumerable<string> providerIds);
    /// Raised once per State change, on the thread that caused it. Not raised for count/latency-only changes.
    public event Action<ProviderHealthSnapshot>? StateChanged;
    /// Smoke test and previews only: sets the displayed snapshot; never touches breakers or buckets.
    public void Seed(ProviderHealthSnapshot snapshot);
}

public static class ProviderHealthText   // contract §8.7: whole-sentence templates with named placeholders
{
    public static string Describe(ProviderHealthSnapshot s, DateTimeOffset now);  // "Working · 340 ms", "Skipping for 18 s: it is limiting requests.", "Your key was rejected. Check it above."
    public static string Summary(IReadOnlyList<ProviderHealthSnapshot> chain, IReadOnlyList<string> names, DateTimeOffset now);
                                         // "Using Google Translate (free)." / "Google Translate (free) is unavailable, so Envoy is using Microsoft Translator (free)."
    public static string RetryButtonName(string providerName);                     // "Retry {provider} now" (AutomationProperties.Name)
    public static string Sanitize(string message, LogRedactor redactor);           // cut to 160 chars, then redactor.Redact
}
```

State rules. Every state change goes through one internal `ProviderHealthEntry.Apply(...)` that compares old and new
state and raises `StateChanged` exactly once per change:

- `Healthy`: last real outcome was a first-attempt success.
- `Degraded`: breaker closed and consecutive failures > 0, or the last success needed a retry.
- `Throttled`: breaker opened by a long `Retry-After` (REQ-TRR-5), or the last call was rejected by the token bucket.
- `CoolingDown`: breaker opened by consecutive failures.
- `AuthFailed` / `QuotaExceeded`: per REQ-TRR-10. `BudgetedTranslator` marks `QuotaExceeded` with `RetryAt` = next
  local midnight when it throws `BudgetExceededException`.
- `Unknown`: never called, after `ResetProvider` or a fingerprint change, or a `Configuration` failure (shown as "Not
  set up"). Build-time construction failures from `TranslatorFactory.Build` are recorded the same way.

State changes only on recorded events (outcomes, local rejections, resets). The passing of `RetryAt` does not by
itself change state, so 07a's `ProviderFailing` (raised on `AuthFailed`/`QuotaExceeded`/`CoolingDown`, resolved on
`Healthy`) sees a clean edge for each change.

### 6. Cache (`TranslationCache.cs`; `TranslationCacheStore.cs` in PR B; `CachingTranslator.cs`, changed)

```csharp
public sealed class TranslationCache(int capacity, TimeProvider clock)
{
    public bool TryGet(string key, out TranslationResult result);
    public void Set(string key, TranslationResult result, bool persistable);
    public int Count { get; }
    public bool IsDirty { get; }
    public int Capacity { get; set; }                     // shrinking evicts LRU
    public void Clear();
    public IReadOnlyList<CacheEntry> Export();            // persistable entries, most recent first
    public void Import(IEnumerable<CacheEntry> entries);  // drops expired (> 30 days)
    public static string Key(string ns, TranslationRequest r);   // ns␟source␟target␟trimmed text
}

public sealed record CacheEntry(string KeyHash, string Text, string? DetectedSourceLanguage, string ProviderId, DateTimeOffset At);

public sealed class TranslationCacheStore(string path, ILog log)   // PR B
{
    public IReadOnlyList<CacheEntry> Load();                         // corrupt → rename to .bad, Warning, return []
    public bool Save(IReadOnlyList<CacheEntry> entries, int maxBytes = 1 << 20);  // temp + File.Move(overwrite); false on IO error (logged)
    public void Delete();
}

// Existing constructor kept for tests/back-compat (private cache, namespace "").
public sealed class CachingTranslator(ITranslator inner, TranslationCache cache, string cacheNamespace) : ITranslator
{
    public CachingTranslator(ITranslator inner, int capacity = 500);
}
```

In memory the key is the plain composite key. On disk only `KeyHash` (SHA-256 hex of the key) is stored. **Privacy
wording (used in the UI caption, `docs/translation-providers.md` and `docs/privacy.md`):** "The file holds
translations of chat lines you received, in your language, plus a one-way fingerprint of each original line. It never
leaves this PC." The hash stops casual reading of the originals; it does not stop someone guessing short common lines,
so the docs do not claim the originals are unrecoverable. Results flagged `FromGlossary` and requests longer than 200
characters are not persistable. File format:
`{"version":1,"entries":[{"k":"…","t":"…","d":"zh-CN","p":"google-free","at":"2026-10-14T…Z"}]}` with invariant
ISO-8601 dates.

### 7. Runtime and composition (`TranslationRuntime.cs`, `TranslationRuntimeOptions.cs`, new; `TranslatorFactory.cs`, `FallbackTranslator.cs`, `UsageTracker.cs`, changed)

```csharp
/// Immutable snapshot built on the UI thread in AppHost.ApplySettings (contract §7).
public sealed record TranslationRuntimeOptions(TimeSpan RequestTimeout, bool RememberTranslations, int CacheMaxEntries)
{
    public static TranslationRuntimeOptions Default { get; }
    public static TranslationRuntimeOptions From(TranslationSettings s);   // clamps 3–60 s, 100–20000 entries
}

/// Long-lived translation state owned by AppHost; survives TranslatorFactory rebuilds.
public sealed class TranslationRuntime : IDisposable
{
    public TranslationRuntime(string? cachePath = null, TimeProvider? clock = null,
                              LogRedactor? redactor = null, ILog? log = null);
    public TranslationRuntimeOptions Options { get; }            // volatile read
    public ProviderHealthRegistry Health { get; }
    public TranslationCache Cache { get; }
    public event Action<ProviderHealthSnapshot>? ProviderHealthChanged;   // = Health.StateChanged; any thread
    public event Action<TranslationDiagnostic>? Diagnostic;               // SHOULD (REQ-TRR-26); any thread

    public void Configure(TranslationRuntimeOptions options);    // Volatile.Write; resize cache; (PR B) load/delete file
    public void RecordBuildFailure(string providerId, Exception error);   // Configuration → Unknown/"Not set up"
    public void ResetProvider(string providerId);                // "Retry now": breaker + buckets + health
    public bool ClearCache();                                    // memory + file (PR B); false if the delete failed (logged)
    public void SuppressPersistence();                           // PR B: waits for an in-flight save, then no more writes
    public void Flush();                                         // PR B: synchronous save if enabled, dirty, not suppressed
    public void Dispose();                                       // Flush + stop timer
    internal ProviderGuard Guard(string providerId, string configFingerprint);
}

public sealed record TranslationDiagnostic(string ProviderId, string Outcome, TranslationErrorKind? Kind,
                                           int? StatusCode, int Attempt, TimeSpan Elapsed, TranslationLane Lane);

/// Core so it can be unit-tested; AppHost calls it once.
public static class TranslationLogging
{
    /// Transitions → Info "Provider state provider={id} state={state} kind={kind} retry_at={t}".
    /// Diagnostics (SHOULD) → Info "Translation attempt provider={id} outcome=… kind=… status=… attempt=… elapsed_ms=…".
    /// No field carries chat text.
    public static IDisposable Attach(TranslationRuntime runtime, ILog log);
}
```

`TranslatorFactory(HttpClient http, UsageTracker usage, TranslationRuntime? runtime = null)`. The existing two-argument
form still works and creates a private runtime. `Build(TranslationSettings s, Action<string, Exception>? onProviderFailed = null, TranslationLane lane = TranslationLane.Chat)`
produces:

```
Caching(runtime.Cache, ns) → Coalescing (SHOULD) → Fallback(deadline from runtime.Options)[
    Budgeted( Resilient( provider, runtime.Guard(id, fingerprint), lane ) ) …, Budgeted(Resilient(GoogleFree)) ]
```

`Build` runs on the UI thread inside `ApplySettings` and reads `TranslationSettings` only there; each provider captures
its values at construction. Construction failures go to `runtime.RecordBuildFailure` and to `onProviderFailed` as
today. The configuration fingerprint is the SHA-256 of the provider's credential and endpoint fields, computed in
memory and never stored or logged.

`FallbackTranslator(IReadOnlyList<ITranslator> chain, Func<TimeSpan>? deadline = null, TimeProvider? clock = null)`:

- Sets `TranslationDeadline` from `deadline()` per call and links a deadline CTS.
- Treats `OperationCanceledException` as `Timeout` when the **caller's** token is not cancelled (REQ-TRR-7).
- Skips `ProviderUnavailableException` without raising `ProviderFailed`; the skip is already in health.
- When the deadline fires, throws `TranslationException(Timeout)` with `TranslationErrorText.Timeout`.
- The combined failure message (`TranslationErrorText.AllFailed`) lists provider names and states only.
- COULD (REQ-TRR-27): forced probe when everything was rejected locally.

`UsageTracker.Refund(string providerId, int characters)` (new, never below 0). `BudgetedTranslator` refunds on
`Unavailable`, `Configuration`, and `Transient` whose `HttpRequestError` is `ConnectionError` or
`NameResolutionError` (request never sent).

### 8. Settings added (`AppSettings.cs` → `TranslationSettings`, shared file)

Additive only (contract §7); `AppSettings.CurrentVersion` stays 1. None of these hold names, chat or positions, so no
`[Personal]`/`[Location]` marker.

| Property | Type | Default | PR | Notes |
| --- | --- | --- | --- | --- |
| `RequestTimeoutSeconds` | `int` | `15` | A | Overall deadline. Clamped to 3–60 in `TranslationRuntimeOptions.From`. UI slider 5–30 (PR B). |
| `RememberTranslations` | `bool` | `false` | B | Opt-in disk cache (privacy: off, like `SaveTranscripts`). |
| `CacheMaxEntries` | `int` | `2000` | A | Memory and disk cap. Clamped to 100–20000. Not shown in the UI. |

Today's in-memory cap is 500; the new default of 2000 entries is a few hundred KB.

### 9. App changes (thin shell)

**`AppHost.cs` (shared).** Positions follow contract §3.

- Properties: `TranslationRuntime TranslationRuntime { get; }`, `ProviderHealthRegistry TranslationHealth => TranslationRuntime.Health`,
  `string TranslationCachePath => Path.Combine(DataFolder, "translation-cache.json")`,
  `IReadOnlyList<string> ActiveProviderIds { get; }` (the chain as built), `ITranslator ComposeTranslator { get; }`
  (SHOULD; falls back to `Translator` when REQ-TRR-29 is not built), and
  `event Action<ProviderHealthSnapshot>? ProviderHealthChanged` (UI thread).
- **Constructor position 3:** `TranslationRuntime = new(TranslationCachePath, Time, Log.Redactor, Log.For("Translation"));`
  `TranslationRuntime.Configure(TranslationRuntimeOptions.From(Settings.Translation));`
  `_translationLogging = TranslationLogging.Attach(TranslationRuntime, Log.For("Translation"));`
  `TranslationRuntime.ProviderHealthChanged += s => _ui.BeginInvoke(() => { RefreshProviderStatus(); ProviderHealthChanged?.Invoke(s); });`
- **Constructor position 4:** `_factory = new TranslatorFactory(_http, Usage, TranslationRuntime);`
- **`ApplySettings` position 2** builds `TranslationRuntimeOptions.From(Settings.Translation)`; **position 3** calls
  `TranslationRuntime.Configure(...)`; **position 4** is `BuildTranslator()`, which builds the chat stack and (SHOULD)
  `ComposeTranslator` with `TranslationLane.Compose`, then calls `RefreshProviderStatus()`.
- `BuildTranslator()` no longer writes `_providerStatus` from `ProviderFailed`. 02's interim
  `onProviderFailed` Warning is replaced by `TranslationLogging` (02 §AppHost notes this hand-over).
  `RefreshProviderStatus()` sets `_providerStatus = ProviderHealthText.Summary(TranslationHealth.Snapshot(ActiveProviderIds), names, Time.GetUtcNow())`.
- `SuppressSave()` (02) also calls `TranslationRuntime.SuppressPersistence()` (PR B), so "Reset settings" and "Delete
  all Envoy data" are never undone by the cache flush in `Dispose` (contract §4; 02's Dispose-time-writer rule).
- `Dispose()`: `_translationLogging.Dispose(); TranslationRuntime.Dispose();` (flush, unless suppressed) — contract §4
  `ExitApp` step 3.
- `Outgoing` (`OutgoingTranslator`) takes `() => ComposeTranslator`.
- SHOULD: raise `HttpClient.Timeout` to 30 s; it is only a backstop now. The default User-Agent is untouched
  (contract §3).
- No new `catch` blocks. Any added later must log (02 rule).

**Diagnostics (02's report, PR A).** `DiagnosticsSnapshot` gains `IReadOnlyList<ProviderHealthSnapshot> TranslationHealth { get; init; } = []`.
`DiagnosticsCollector.Collect(Host)` fills it with `Host.TranslationHealth.Snapshot(Host.ActiveProviderIds)`.
`DiagnosticsReport.Render` writes one line per provider in its existing Translation section:
`{id}: {state}, median {ms} ms, ok {n}, failed {n}, last error kind {kind} ({status})` — kind and status only, never
`LastError` text.

**`Windows/ComposeWindow.xaml.cs`** (SHOULD, REQ-TRR-29): the back-translation call uses `_host.ComposeTranslator`
instead of `_host.Translator`. One line; 07c later edits this file for other reasons.

**Translation page (PR B)** — `src/Envoy.App/Pages/TranslationPage.xaml` and `ViewModels/MainViewModel.Translation.cs`:

- **"Provider health" card** under the provider pickers: an `ItemsControl` bound to
  `ObservableCollection<ProviderHealthRow> ProviderHealthRows`. `ProviderHealthRow` (`ViewModels/`) has `ProviderId`,
  `Name`, `StateText` (from `ProviderHealthText.Describe`), `LastSuccessText`, `State` and `RetryName`. A
  `DataTrigger` on `State` picks the dot brush from existing palette keys only: Healthy → `Success`;
  Degraded/Throttled/CoolingDown → `Accent`; AuthFailed/QuotaExceeded → `Danger`; Unknown → `Text.Muted`, all as
  `DynamicResource`. Each row's "Retry now" button binds `RetryProviderCommand` (`RelayCommand<string>`, parameter
  provider id → `Host.TranslationRuntime.ResetProvider`) and `AutomationProperties.Name="{Binding RetryName}"`. The
  dot is decorative (`AutomationProperties.Name` on the row text, not the dot).
- Rows rebuild on `Host.ProviderHealthChanged` and on settings rebuilds, and refresh text on a 1 s `DispatcherTimer`
  that runs only while `IsTranslationPageActive` is true. `TranslationPage` code-behind only sets that flag from
  `IsVisibleChanged` (01's pattern). No `Click=` handlers.
- **"Remember translations between sessions" checkbox** bound in the page to `Settings.Translation.RememberTranslations`,
  with the privacy caption from §6 and `"{count} translations saved on this PC."`, plus a "Clear saved translations"
  button (`ClearTranslationCacheCommand`, `RelayCommand`; on `false` it sets a status line from
  `TranslationErrorText.CacheClearFailed`).
- **"Give up after" slider** (5–30 s) bound to `Settings.Translation.RequestTimeoutSeconds` (SHOULD, REQ-TRR-28).
- Names: `AutomationProperties.Name` on the checkbox, button and slider. No new palette keys.

**`SmokeTest.cs` (shared, PR B).** Placed per contract §6:

- **Setup phase, after the translation-provider and compose waits:** section `translation-health-seed` calls
  `host.TranslationHealth.Seed(...)` for the main host's `ActiveProviderIds` (default chain: `google-free` →
  `Healthy` with a 340 ms median, `edge-free` → `CoolingDown` with `RetryAt = now + 60 s`). `Seed` is display-only, so
  the compose window's real calls are unaffected.
- **Per palette, step 3 (MainWindow still open):** section `translation-health-rows` takes
  `MainWindow.PageFor(PageIds.Translation)`, finds the health `ItemsControl`, and asserts it generated
  `host.ActiveProviderIds.Count` containers and that `vm.ProviderHealthRows.Count` matches. It logs
  `[smoke] translation-health-rows ok elapsed_ms=<n> rows=<n>`. Binding errors fail the run through 01's listener.
- The `{palette}-settings-translation.png` screenshot from 01's page loop shows the card. No extra screenshot.
- The smoke host's `DataFolder` is the temp folder from 02, so `Configure` (which deletes a cache file when remember is
  off) never touches the developer's real `%APPDATA%\Envoy`.

### 10. Allowlists, analyzers and policy (contract §5)

- **P/Invoke additions:** none. `Native.cs` and `policy.json → native` are unchanged.
- **Network allowlist:** no new path and no new host. Network types (`HttpClient`, `HttpRequestMessage`) appear only
  in `src/Envoy.Core/Translation/**` and `src/Envoy.App/AppHost.cs`, both already allowed. `ProviderHttp` names
  `HttpResponseMessage`, which is not a policed type. Provider URLs are unchanged literals
  (`translate.googleapis.com`, `edge.microsoft.com`, `api-edge.cognitive.microsofttranslator.com`, `api.deepl.com`,
  `api-free.deepl.com`, all already in `hosts.allowed`); Claude and AWS endpoints live inside their SDKs. Test fakes
  and fixtures are under `tests/`, outside the policy scan.
- **Banned APIs:** none used. No process launching.
- **Analyzers:** new code is clean under 08a's rules with `CI=true`: tokens forwarded (CA2016), user text formatted
  with `CultureInfo.CurrentCulture` and file data with `CultureInfo.InvariantCulture` (CA1305), ordinal string
  comparisons (CA1307/CA1309/CA1310), `IDisposable` owners dispose their CTS/timers.

### Threading

- Core types are thread-safe: one lock per guard for breaker and bucket, `SemaphoreSlim` per lane gate, lock-protected
  dictionaries in `CoalescingTranslator` and `TranslationCache`, `Interlocked` counters in health. No lock is held
  across an `await`.
- `ProviderHealthChanged` and `Diagnostic` fire on the thread that finished the attempt. `AppHost` marshals to the UI
  dispatcher; `TranslationLogging` writes to `ILog`, which is thread-safe (02).
- Background code never reads `AppSettings`: the runtime reads its `volatile` `TranslationRuntimeOptions`; providers
  hold values captured at build time on the UI thread.
- PR B: the disk cache loads synchronously in `Configure` when remember turns on (≤ 1 MiB) and saves from a
  `TimeProvider.CreateTimer` callback on the thread pool, under a `SemaphoreSlim(1)` shared with `Flush`,
  `ClearCache` and `SuppressPersistence`, so a save never overlaps a delete.

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| Primary provider times out | Timeout after `AttemptTimeout`, one retry if the deadline allows, then the next provider. The message ends `Translated` or `Failed`, never stuck `Pending`. |
| Google returns 429 with `Retry-After: 120` | SHOULD (REQ-TRR-5): no wait, Google is `Throttled` for 120 s, Edge answers. Without REQ-TRR-5: backoff retry, then breaker rules. |
| Endpoint returns HTML | `BadResponse`, fall through. Health row: "Google Translate (free) sent a reply Envoy could not read." No JSON parser text anywhere. |
| Wrong DeepL key | One call: `Auth` → `AuthFailed`; breaker stays open until the key changes or "Retry now". Fallback serves chat. No refund (the request reached DeepL). 07a raises `ProviderFailing`. |
| Every provider down | Each message fails within the deadline: "Translation unavailable: Google Translate (free) is cooling down, Microsoft Translator (free) is limiting requests." |
| Burst of 8 chat lines on Google | 6 immediate, 2 within ~1 s from the bucket; no fall-through from the burst alone. |
| Reply box typing during a burst | SHOULD (REQ-TRR-29): compose uses its own lane; chat tokens untouched. |
| Disk cache corrupt (PR B) | Renamed to `translation-cache.json.bad`, Warning logged, empty cache, no crash. |
| Disk full or file locked on save (PR B) | Save skipped and logged at Warning (repeat-suppressed), cache stays dirty, retried on the next tick. |
| "Delete all Envoy data" (02) | `SuppressSave` → `SuppressPersistence`; the file is deleted by `DataInventory.DeleteAll` and not rewritten at exit. |

## Acceptance criteria

All unit tests are in `tests/Envoy.Core.Tests/Translation/` and run on Linux and in the `check` job.

| ID | Requirements | Kind | Verified by |
| --- | --- | --- | --- |
| AC-TRR-1 | 1, 2 | unit | `ProviderHttpTests.Classify_maps_status_codes` (Theory over 400/401/403/404/408/413/414/422/429/456/500/502/503/504/529), `ParseRetryAfter_accepts_seconds_and_http_date`, `ParseRetryAfter_clamps_to_one_hour`, `Parse_wraps_json_errors_as_BadResponse`; `TranslationErrorTextTests.Templates_have_named_placeholders_only` (no `{0}`, no "Error:"). |
| AC-TRR-2 | 4 | unit | `RetryPolicyTests.Backoff_is_full_jitter_and_capped`, `Never_retries_non_retryable_kinds` (Theory), `Retry_never_exceeds_deadline`; `ResilientTranslatorTests.Retries_transient_until_MaxAttempts_then_throws`. |
| AC-TRR-3 | 5 (SHOULD) | unit | `ResilientTranslatorTests.Waits_short_Retry_After_then_succeeds`, `Long_Retry_After_marks_throttled_and_throws_without_waiting`. |
| AC-TRR-4 | 6, 7 | unit | `ResilientTranslatorTests.Attempt_timeout_becomes_Timeout_kind`; `FallbackDeadlineTests.HttpClient_timeout_falls_through_to_next_provider`, `Overall_deadline_throws_Timeout_not_OperationCanceled`, `Caller_cancellation_still_throws_OperationCanceled`, `Deadline_follows_Configure`; `TranslationRuntimeOptionsTests.Snapshot_is_independent_of_settings` (contract §7), `From_clamps_out_of_range_values`. |
| AC-TRR-5 | 7 (end to end) | unit | `FallbackDeadlineTests.Pipeline_marks_message_failed_when_all_providers_time_out`: today's `ChatPipeline` over a factory-built stack whose fake handler never answers ends `IncomingStatus.Failed` (not `Pending`) once the fake clock passes the deadline. |
| AC-TRR-6 | 8 | unit | `CircuitBreakerTests.Opens_after_threshold_consecutive_failures`, `Success_resets_consecutive_count`, `Allows_single_half_open_probe`, `Probe_success_closes_and_resets_backoff`, `Probe_failure_doubles_open_duration_up_to_max`, `Cancelled_probe_is_not_a_failure`; `ResilientTranslatorTests.Rejects_while_open_without_calling_inner`. |
| AC-TRR-7 | 10 | unit | `CircuitBreakerTests.Auth_opens_immediately_until_reset`, `Quota_opens_until_retry_after_or_one_hour`, `Unsupported_configuration_and_cancellation_do_not_count`; `ProviderHealthTests.Budget_exceeded_marks_quota_until_midnight`. |
| AC-TRR-8 | 11 | unit | `TranslationRuntimeTests.Breaker_state_survives_factory_rebuild`, `Fingerprint_change_resets_provider`, `ResetProvider_closes_breaker_and_clears_health`. |
| AC-TRR-9 | 12 | unit | `TokenBucketTests.Burst_then_refills_at_rate`, `TakeAsync_gives_up_after_max_wait`, `TakeAsync_honours_cancellation`; `ResilientTranslatorTests.Throttled_request_falls_through_without_network_call`. |
| AC-TRR-10 | 13 (SHOULD) | unit | `ResilientTranslatorTests.Never_exceeds_MaxConcurrency`, `Queue_overflow_fails_fast`. |
| AC-TRR-11 | 14, 15 (SHOULD) | unit | `CoalescingTranslatorTests.*` (five tests in the test plan); `ResilientTranslatorTests.Cancelled_while_queued_makes_no_call`. |
| AC-TRR-12 | 16 | unit | `ProviderHealthTests.Records_success_latency_median_of_last_20`, `State_transitions_follow_breaker`, `StateChanged_raised_once_per_transition_only`, `Local_rejection_transition_raises_StateChanged`, `Build_failure_shows_not_set_up`; `ProviderHealthTextTests.Describe_formats_each_state` (Theory), `Summary_names_active_provider_when_primary_unavailable`; `TranslationLoggingTests.Logs_each_transition_at_Info_under_Translation`; `TranslationDiagnosticsReportTests.Report_lists_health_for_active_chain_without_error_text`. |
| AC-TRR-13 | 17 | unit | `ProviderHealthTests.Last_error_never_contains_request_text_or_key`: a fake handler whose body echoes the text and key leaves `LastError`, every `TranslationDiagnostic`, every `TranslationLogging` entry (captured via 02's `MemoryLogSink`) and the `FallbackTranslator` aggregate message free of both strings; `ProviderHealthTextTests.Sanitize_uses_LogRedactor_mask` asserts `[redacted]` and the 160-char cut. |
| AC-TRR-14 | 18, 28 | typecheck + check_resources + smoke | `tools/typecheck/typecheck.sh` passes (bindings to `ProviderHealthRows`, `RetryProviderCommand`, `ClearTranslationCacheCommand`; `Success`/`Accent`/`Danger`/`Text.Muted` resolve in every palette). The Windows smoke test logs `[smoke] translation-health-seed ok` once and `[smoke] translation-health-rows ok` for **every** `ThemeManager.PaletteIds` entry, with zero binding errors; `{palette}-settings-translation.png` exists for each palette. `ProviderHealthTextTests.Summary_*` covers the `ProviderStatus` text. |
| AC-TRR-15 | 19 | unit | `TranslationCacheTests.Cache_survives_rebuild_in_runtime`, `Switching_primary_provider_misses_cache`, `Lru_evicts_oldest_at_capacity`. |
| AC-TRR-16 | 20, 21 (SHOULD) | unit + manual | `TranslationCacheStoreTests.*` and `TranslationRuntimeTests.Disabling_remember_deletes_file`, `Flush_on_dispose_writes_when_dirty`, `SuppressPersistence_then_Dispose_does_not_write`, `Clear_waits_for_inflight_save`; `DataInventoryTests.Every_item_is_documented_in_privacy_md` (02's test) stays green with the new items. Manual M3. |
| AC-TRR-17 | 22 | unit | `BudgetTests.Local_rejection_refunds_characters`, `Connection_failure_refunds_characters`, `Timeout_is_not_refunded`, `Retries_reserve_characters_once`. |
| AC-TRR-18 | 23, 24 | unit | `ProviderContractTests` (one Theory per provider × scenario). The Claude test asserts exactly one request reaches the handler on a 500 when called directly (SDK retries off). `AwsTranslatorTests.Config_disables_sdk_retries` asserts `MaxErrorRetry == 0` and the timeout. |
| AC-TRR-19 | 25 (SHOULD) | unit | `ProviderContractTests.Edge_refreshes_token_once_on_401`, `Edge_second_401_is_RateLimited`. |
| AC-TRR-20 | 26 (SHOULD) | unit | `TranslationRuntimeTests.Diagnostic_raised_per_attempt_with_kind_and_status`; `TranslationLoggingTests.Logs_diagnostic_at_Info_without_text`. |
| AC-TRR-21 | 27 (COULD) | unit | `FallbackDeadlineTests.All_open_forces_single_probe_on_earliest_retry` (only if built). |
| AC-TRR-22 | all | CI check | `dotnet test tests/Envoy.Core.Tests` and `tools/typecheck/typecheck.sh` green with `CI=true` (analyzers as errors); `tools/ci/check_policy.py` green with **no** `policy.json` change. Existing `TranslatorStackTests` pass unchanged (back-compat constructors). |
| AC-TRR-23 | 29 (SHOULD) | unit | `ResilientTranslatorTests.Compose_lane_never_takes_chat_tokens`, `Compose_lane_shares_breaker_and_health`. |

## Test plan

New test files go in `tests/Envoy.Core.Tests/Translation/`, all xUnit, using `FakeTimeProvider` from
`Microsoft.Extensions.TimeProvider.Testing` (already referenced). No test touches the real network or waits on real
time.

**Helpers**

- `Fakes/FakeHttpHandler.cs`: records every request (method, URI, headers, body) and answers from a queue of
  `Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>`. Helpers: `Respond(fixture)`, `Hang()`,
  `Throw(new HttpRequestException(HttpRequestError.ConnectionError, …))`.
- `Fakes/ProviderFixture.cs`: loads `Fixtures/Providers/<provider>/<scenario>.json` with the shape
  `{ "status": 429, "headers": {"Retry-After": "2"}, "contentType": "application/json", "body": "…" }`.
- `Fakes/ScriptedTranslator.cs`: an `ITranslator` with a programmable sequence of results or exceptions, an optional
  gate and an in-flight counter. The existing `FakeTranslator` is kept.
- Fixtures for `google-free`, `edge-free` (plus `auth-token.txt`), `deepl`, `libretranslate`, `claude` and `aws`, each
  with `success`, `429`, `500`, `malformed` and `auth` (Google: `403`; Edge: `401`). The csproj adds
  `<None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />`. `Fixtures/README.md` records provenance.

**Unit tests** (PR A unless marked B)

| File | Tests | Asserts |
| --- | --- | --- |
| `ProviderHttpTests.cs` | `Classify_maps_status_codes`, `ParseRetryAfter_accepts_seconds_and_http_date`, `ParseRetryAfter_clamps_to_one_hour`, `Parse_wraps_json_errors_as_BadResponse` | REQ-TRR-2 table; `"2"` → 2 s, RFC 1123 date +30 s → 30 s; `"99999"` → 3600 s, past date → 0; HTML/`{}`/`[]` → `BadResponse` with no body text in the message |
| `TranslationErrorTextTests.cs` | `Templates_have_named_placeholders_only` | reflection over every `const string`: placeholders are `{name}`, no `Error:` prefix |
| `RetryPolicyTests.cs` | `Backoff_is_full_jitter_and_capped`, `Never_retries_non_retryable_kinds`, `Retry_never_exceeds_deadline` | jitter 1 at attempts 1/2/5 → 250/500/2000 ms; null for non-retryable kinds; remaining 1 s with Retry-After 2 s → null |
| `CircuitBreakerTests.cs` | `Opens_after_threshold_consecutive_failures`, `Success_resets_consecutive_count`, `Allows_single_half_open_probe`, `Probe_success_closes_and_resets_backoff`, `Probe_failure_doubles_open_duration_up_to_max`, `Cancelled_probe_is_not_a_failure`, `Auth_opens_immediately_until_reset`, `Quota_opens_until_retry_after_or_one_hour`, `Unsupported_configuration_and_cancellation_do_not_count` | 3rd failure → Open until +20 s; F,F,S,F,F → Closed; 20 → 40 → 80 … ≤ 5 min; Auth still open after +1 day |
| `TokenBucketTests.cs` | `Burst_then_refills_at_rate`, `TakeAsync_gives_up_after_max_wait`, `TakeAsync_honours_cancellation` | 6 takes then fail; +500 ms → 1 more |
| `ResilientTranslatorTests.cs` | `Retries_transient_until_MaxAttempts_then_throws`, `Succeeds_on_retry_and_reports_degraded`, `Attempt_timeout_becomes_Timeout_kind`, `Rejects_while_open_without_calling_inner`, `Throttled_request_falls_through_without_network_call`, `Non_translation_exceptions_are_wrapped`; SHOULD: `Waits_short_Retry_After_then_succeeds`, `Long_Retry_After_marks_throttled_and_throws_without_waiting`, `Never_exceeds_MaxConcurrency`, `Queue_overflow_fails_fast`, `Cancelled_while_queued_makes_no_call`, `Compose_lane_never_takes_chat_tokens`, `Compose_lane_shares_breaker_and_health` | as named; peak in-flight == 3 for `google-free` |
| `CoalescingTranslatorTests.cs` (SHOULD) | `Identical_concurrent_requests_make_one_call`, `Different_keys_are_not_coalesced`, `One_waiter_cancelling_does_not_cancel_others`, `All_waiters_cancelling_cancels_inner_call`, `Failure_is_shared_and_entry_removed` | |
| `FallbackDeadlineTests.cs` | `HttpClient_timeout_falls_through_to_next_provider`, `Caller_cancellation_still_throws_OperationCanceled`, `Overall_deadline_throws_Timeout_not_OperationCanceled`, `Deadline_follows_Configure`, `Unavailable_providers_skipped_without_ProviderFailed_event`, `Pipeline_marks_message_failed_when_all_providers_time_out`; COULD: `All_open_forces_single_probe_on_earliest_retry` | |
| `TranslationRuntimeOptionsTests.cs` | `Snapshot_is_independent_of_settings`, `From_clamps_out_of_range_values` | mutate `TranslationSettings` after `From` → snapshot unchanged |
| `ProviderHealthTests.cs` | `Records_success_latency_median_of_last_20`, `State_transitions_follow_breaker`, `StateChanged_raised_once_per_transition_only`, `Local_rejection_transition_raises_StateChanged`, `Build_failure_shows_not_set_up`, `Last_error_never_contains_request_text_or_key`, `Budget_exceeded_marks_quota_until_midnight`, `Seed_does_not_touch_breaker` | Healthy → Degraded → CoolingDown → Healthy, one event each; latency-only change → no event |
| `ProviderHealthTextTests.cs` | `Describe_formats_each_state`, `Summary_names_active_provider_when_primary_unavailable`, `Sanitize_uses_LogRedactor_mask` | |
| `TranslationLoggingTests.cs` | `Logs_each_transition_at_Info_under_Translation`, `Logs_diagnostic_at_Info_without_text` | via `LogHub` + `MemoryLogSink` |
| `TranslationDiagnosticsReportTests.cs` | `Report_lists_health_for_active_chain_without_error_text` | `DiagnosticsReport.Render` output contains state/kind lines and not `LastError` |
| `TranslationCacheTests.cs` | `Lru_evicts_oldest_at_capacity`, `Cache_survives_rebuild_in_runtime`, `Switching_primary_provider_misses_cache` | |
| `TranslationCacheStoreTests.cs` (B) | `Roundtrip_preserves_entries_and_lru_order`, `Save_is_atomic_and_capped_at_max_bytes`, `Expired_entries_dropped_on_import`, `Long_messages_not_persisted`, `Corrupt_file_renamed_to_bad`, `Disk_file_contains_only_key_hashes` | temp dir; no `.tmp` left |
| `TranslationRuntimeTests.cs` | `Breaker_state_survives_factory_rebuild`, `Fingerprint_change_resets_provider`, `ResetProvider_closes_breaker_and_clears_health`, `Diagnostic_raised_per_attempt_with_kind_and_status`; B: `Disabling_remember_deletes_file`, `Flush_on_dispose_writes_when_dirty`, `SuppressPersistence_then_Dispose_does_not_write`, `Clear_waits_for_inflight_save` | |
| `BudgetTests.cs` | `Local_rejection_refunds_characters`, `Connection_failure_refunds_characters`, `Timeout_is_not_refunded`, `Retries_reserve_characters_once` | |
| `ProviderContractTests.cs` | `Google_contract`, `Edge_contract`, `DeepL_contract`, `Libre_contract`, `Claude_contract`, `Aws_contract` (Theories over scenarios); SHOULD: `Edge_refreshes_token_once_on_401`, `Edge_second_401_is_RateLimited` | Google: GET `translate_a/single`, `client=gtx`, `tl`, `q` escaped; 403 → `RateLimited`. Edge: auth GET then POST `…/translate?api-version=3.0&to=…`, `Authorization: Bearer`. DeepL: `:fx` → `api-free.deepl.com`, `DeepL-Auth-Key`, 456 → `QuotaExceeded`. Libre: POST `{base}/translate`, 400 → `Unsupported`. Claude: POST `/v1/messages`, `x-api-key`, one request per call. AWS: `X-Amz-Target` header, error codes → kinds |
| `AwsTranslatorTests.cs` | `Config_disables_sdk_retries` | `MaxErrorRetry == 0`, timeout set |

**Smoke-test additions** (`SmokeTest.cs`, PR B): sections `translation-health-seed` (setup) and
`translation-health-rows` (per palette, MainWindow open), as in Design §9. Expected cost: under 50 ms per palette.

**Manual checks (Windows)** — collected into `docs/releasing.md` by 08b (contract §8.3).

- **M1.** Disconnect the network, type in the Translation test box, press Translate. Within 15 s you see a failure,
  not an endless "…". The health card shows Google and Microsoft "Cooling down". Reconnect and press "Retry now"; the
  row turns green on the next translation.
- **M2.** Put an invalid DeepL key as primary. The first message is translated by the fallback; the DeepL row is red
  ("Your key was rejected"). Fix the key; the row returns to "Not used yet", then "Working".
- **M3.** (SHOULD) Turn on "Remember translations", translate "gg wp", quit, restart. The result is instant. The file
  `translation-cache.json` exists in the data folder and does not contain "gg wp". Turn the option off; the file is
  gone. Turn it on again, translate, then About › "Delete all Envoy data…": after the restart the file is absent.
- **M4.** In a live match, or with the demo feed, send a burst of ≥ 8 chat lines. All are translated, and the War
  Room status never shows Google as throttled from the burst alone.

## Files touched

| Path | Change | PR | Shared? |
| --- | --- | --- | --- |
| `src/Envoy.Core/Translation/ITranslator.cs` | `TranslationException` gains `Kind`/`StatusCode`/`RetryAfter`; `BudgetExceededException` kind | A | |
| `src/Envoy.Core/Translation/TranslationErrors.cs` | new: `TranslationErrorKind`, `ProviderUnavailableException` | A | |
| `src/Envoy.Core/Translation/TranslationErrorText.cs` | new: user-facing sentence templates | A | |
| `src/Envoy.Core/Translation/Providers/ProviderHttp.cs` | new | A | |
| `src/Envoy.Core/Translation/Providers/{GoogleFree,EdgeFree,DeepL,LibreTranslate,Claude,Aws}Translator.cs` | `ProviderHttp`, kinds, SDK retries off, injected `HttpClient` | A | |
| `src/Envoy.Core/Translation/Resilience/{ResilienceOptions,RetryPolicy,CircuitBreaker,TokenBucket,ProviderGuard,ResilientTranslator,TranslationDeadline}.cs` | new | A | |
| `src/Envoy.Core/Translation/Resilience/ProviderHealthRegistry.cs`, `ProviderHealthText.cs` | new: `ProviderState`, `ProviderHealthSnapshot`, registry, text | A | |
| `src/Envoy.Core/Translation/CoalescingTranslator.cs` | new (SHOULD) | A | |
| `src/Envoy.Core/Translation/TranslationCache.cs` | new | A | |
| `src/Envoy.Core/Translation/TranslationCacheStore.cs` | new (SHOULD) | B | |
| `src/Envoy.Core/Translation/TranslationRuntime.cs`, `TranslationRuntimeOptions.cs`, `TranslationLogging.cs` | new | A (persistence members B) | |
| `src/Envoy.Core/Translation/CachingTranslator.cs` | `TranslationCache` + namespace; old ctor kept | A | |
| `src/Envoy.Core/Translation/FallbackTranslator.cs` | deadline, timeout fall-through, skip-unavailable | A | |
| `src/Envoy.Core/Translation/TranslatorFactory.cs` | optional runtime; lane; new stack order; fingerprints; build failures to the registry | A | |
| `src/Envoy.Core/Translation/UsageTracker.cs` | `Refund`; `BudgetedTranslator` refunds and marks quota health | A | |
| `src/Envoy.Core/Diagnostics/DiagnosticsReport.cs` (+ `DiagnosticsSnapshot`) | `TranslationHealth` field and Translation-section lines | A | **yes** (02's file) |
| `src/Envoy.Core/Diagnostics/DataInventory.cs` | items `translation-cache.json`, `.tmp`, `.bad` | B | **yes** (02's file) |
| `src/Envoy.Core/Settings/AppSettings.cs` | `TranslationSettings`: `RequestTimeoutSeconds`, `CacheMaxEntries` (A); `RememberTranslations` (B) | A, B | **yes** |
| `src/Envoy.App/AppHost.cs` | constructor positions 3–4; `ApplySettings` positions 2–4; `TranslationRuntime`, `TranslationHealth`, `TranslationCachePath`, `ActiveProviderIds`, `ComposeTranslator`, `ProviderHealthChanged`; `TranslationLogging`; `SuppressSave` → `SuppressPersistence`; dispose | A (B: suppress) | **yes** |
| `src/Envoy.App/Services/DiagnosticsCollector.cs` | fill `TranslationHealth` | A | **yes** (02's file) |
| `src/Envoy.App/Windows/ComposeWindow.xaml.cs` | back-translation via `ComposeTranslator` (SHOULD) | A | **yes** |
| `src/Envoy.App/Pages/TranslationPage.xaml(.cs)` | health card, remember checkbox + clear button, slider; `IsVisibleChanged` flag | B | |
| `src/Envoy.App/ViewModels/MainViewModel.Translation.cs` | `ProviderHealthRows`, `RetryProviderCommand`, `ClearTranslationCacheCommand`, cache text, `IsTranslationPageActive` timer | B | |
| `src/Envoy.App/ViewModels/ProviderHealthRow.cs` | new | B | |
| `src/Envoy.App/SmokeTest.cs` | `translation-health-seed`, `translation-health-rows` sections | B | **yes** |
| `tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj` | copy `Fixtures\**` | A | **yes** |
| `tests/Envoy.Core.Tests/Translation/**`, `Fakes/**`, `Fixtures/Providers/**` | new tests, helpers, fixtures | A, B | |
| `docs/translation-providers.md` | "When a provider fails" (A); "Remembering translations" with the §6 privacy wording (B) | A, B | |
| `docs/privacy.md` | "What Envoy stores on your PC": `translation-cache.json` line | B | **yes** (02/07a) |
| `docs/architecture.md` | `ITranslator` stack line, `Translation/` row, provider-health ownership | A | **yes** |
| `CLAUDE.md` | rule: providers throw only classified `TranslationException`s built from `TranslationErrorText`; new providers get `ProviderHttp` + contract fixtures | A | **yes** |
| `CHANGELOG.md` | `## Unreleased` entry per PR | A, B | **yes** |

Not touched: `Interop/Native.cs`, `tools/ci/policy.json`, `BannedSymbols.txt`, `ChatPipeline.cs`, `ChatWatcher.cs`.

## Risks & open questions

1. **Size.** About 1,400 lines of production code and tests plus fixtures, split into PR A (~950) and PR B (~450).
   If PR A runs long, the SHOULD items (13, 14, 15, 25, 26, 29) move to a follow-up without breaking any MUST.
2. **Anthropic SDK details.** It is assumed that `AnthropicClient` accepts `HttpClient`, `MaxRetries` and `Timeout`
   (present in `Anthropic` 12.55.0's public API) and throws the typed exceptions listed. Whether `Retry-After` is
   reachable from `AnthropicRateLimitException` is unverified; if not, Claude 429s use backoff and the breaker only.
   The success fixture must be a complete Messages API object in case the SDK validates strictly.
3. **AWS contract tests** depend on `AmazonTranslateConfig.HttpClientFactory` working with a fake handler and SigV4
   signing without network. REQ-TRR-24 allows the constructed-exception fallback plus `Config_disables_sdk_retries`.
4. **Fixture provenance.** Fixtures are written from the documented formats and the current parse tests. Real
   captures (keys and chat removed) should replace them; `Fixtures/README.md` tracks this.
5. **Default limits for keyless endpoints are guesses.** 2 req/s and a 20 s breaker are not published limits. Tune
   `ResilienceOptions.For` in code if users report throttling; they do not become settings without evidence.
6. **Keyless endpoints and terms of use.** The default Google `client=gtx` and Edge endpoints are unofficial. This area
   only makes Envoy a gentler client; disclosure and consent are 07a's (contract §8.1, the privacy gate in
   `AppHost` before `BuildTranslator` is used).
7. **`AsyncLocal` deadline.** Keeps `ITranslator` unchanged, but is invisible to translators that start work on a fresh
   `ExecutionContext` (none do today).
8. **Per-attempt Info logging volume.** One line per attempt is single digits per second in a busy match; 02's
   `RepeatSuppressor` collapses repeats. If logs grow too large, successful attempts can drop to Debug (recorded as
   a deviation).

## Revision notes

Changes made when revising this spec against `00-integration.md` and the set critique:

- **Single owner of provider health (critique major; contract §2.1).** Stated as a goal and in REQ-TRR-16. Added
  `TranslationRuntime.ProviderHealthChanged(ProviderHealthSnapshot)` raised on every state transition, including
  local rejections, and only on transitions; `AppHost.TranslationHealth`, `AppHost.ProviderHealthChanged` (UI thread)
  and `MainViewModel.ProviderHealthRows`. Removed every `ProviderHealth`-named member: the file is now
  `ProviderHealthRegistry.cs`, the guard holds an internal `ProviderHealthEntry`, and the VM collection was renamed.
  Defined state rules so 07a's `ProviderFailing` gets clean edges.
- **Redaction (contract §2.1 table).** `ProviderHealthText.Sanitize` now delegates to 02's `LogRedactor` (mask
  `[redacted]`); the `***` mask and secrets list are gone.
- **Newly assigned concerns.** 03 now adds the `Translation` log subscription (`TranslationLogging`, Core and
  unit-tested), replaces 02's interim `onProviderFailed` Warning, and adds its health snapshot to 02's diagnostics
  report (02 lands first and leaves that slot). The cache file joins `DataInventory` and `docs/privacy.md`.
  `TranslationCachePath` is an instance property derived from `DataFolder`.
- **Reset/delete not undone by the cache flush (contract §4).** Added `TranslationRuntime.SuppressPersistence()`,
  called from `AppHost.SuppressSave()`, with a test and a manual step.
- **Smoke tests could delete a developer's cache (critique major).** Resolved by 02's temp `DataFolder`; noted in
  §9. Smoke sections follow contract §6 (seed in setup, rows per palette with MainWindow open); AC-TRR-14 now names
  `ThemeManager.PaletteIds`, not "5 themes".
- **Compose starving chat (critique minor; contract §8.8).** New REQ-TRR-29 (SHOULD): separate compose lane with its
  own bucket and slots, sharing breaker, health and cache.
- **Privacy wording of the disk cache (critique minor).** Reworded: the file holds translations of other players'
  chat plus a one-way fingerprint, and the hash is not oversold.
- **Settings thread safety (contract §7).** Added the `TranslationRuntimeOptions` snapshot built in `ApplySettings`,
  read through a volatile field, with a snapshot-independence test; positions in the constructor and `ApplySettings`
  follow contract §3. `TimeProvider` comes from `AppHost.Time`.
- **UI strings (contract §8.7).** User-visible messages moved to `TranslationErrorText`/`ProviderHealthText` as
  whole-sentence named-placeholder templates; no raw exception text reaches the overlay or health row.
- **Accessibility and 01 patterns.** Every new control has `AutomationProperties.Name`; the page timer follows 01's
  `IsVisibleChanged` flag pattern; no `Click=`; the page and VM partial exist (the "before 01 lands" fallbacks are gone).
- **Scope (contract §9).** MUSTs cut from 25 to 16: REQ-TRR-3 folded into 2, 9 into 8; 5, 13, 14, 15 demoted to
  SHOULD; 20, 21, 25, 26, 28 stay SHOULD; 27 stays COULD. Every MUST maps to a unit test, typecheck, a named smoke
  section or a CI check. The area is split into PR A and PR B as the contract requires.
- **Budget refunds (cheap fix found in review).** Timeouts are no longer refunded, because a slow paid call may still
  have been billed; only requests that never left the PC are refunded.
- **Allowlists (contract §5).** Added §10: no P/Invoke, no new network path, no new host, no banned API; CA1001
  justification updated now that Claude uses the shared `HttpClient`.
- **Removed resolved open questions** about merge order with 01, 02 and 04, and the onboarding cache suggestion. The
  pending-message lifecycle on caller cancellation is explicitly 04's (contract §2.6).
