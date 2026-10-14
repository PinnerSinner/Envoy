# Translation resilience: retries, circuit breakers, throttling, coalescing, health and a lasting cache

Status: Proposed

Area id: `03-translation-resilience` · Requirement prefix: `TRR` · Code lives in `src/Envoy.Core/Translation/` and
`tests/Envoy.Core.Tests/`. The app changes are small: composition in `AppHost`, plus one health card and one cache
toggle on the Translation page. `ITranslator` keeps its current signature.

## Context

### What exists today

- **Interface.** `src/Envoy.Core/Translation/ITranslator.cs` defines `ITranslator { Id; IsMetered; TranslateAsync(TranslationRequest, CancellationToken) }`,
  the records `TranslationRequest` and `TranslationResult`, `TranslationException(string message, Exception? inner)`,
  and `BudgetExceededException`. Exceptions have no error category, so a caller cannot tell "key rejected" from
  "server hiccup".
- **The stack** (`TranslatorFactory.Build`, `TranslatorFactory.cs`) is
  `Caching( Fallback[ Budgeted(primary), Budgeted(fallback…), GoogleFree ] )`.
  `AppHost.ApplySettings()` (`src/Envoy.App/AppHost.cs`, line ~178) calls `BuildTranslator()` after **every**
  debounced settings edit. Each rebuild creates a new `CachingTranslator`, so every settings edit empties the
  in-memory cache.
- **Providers** (`Providers/*.cs`):
  - `GoogleFreeTranslator`, `EdgeFreeTranslator`, `DeepLTranslator` and `LibreTranslateTranslator` make one call on
    the shared `HttpClient` that `AppHost` creates with `Timeout = 12 s`. A non-2xx status becomes a plain
    `TranslationException` with a message. `Retry-After` is ignored. DeepL special-cases 403 and 456.
    `EdgeFreeTranslator` clears its token on 401 but does not retry.
  - Each provider's `Parse` calls `JsonDocument.Parse` and `GetProperty`/indexers directly. A malformed body (for
    example an HTML captcha page from the keyless Google endpoint) escapes as `JsonException`,
    `KeyNotFoundException` or `IndexOutOfRangeException`. The overlay then shows text like
    `'<' is an invalid start of a value`.
  - `ClaudeTranslator` builds its own `AnthropicClient` with the SDK defaults: SDK-internal retries, SDK timeout, its
    own `HttpClient`. It wraps every non-cancellation exception in `TranslationException`. `AwsTranslator` uses
    `AmazonTranslateClient` with SDK-default retries and maps `AmazonServiceException` only. Neither can be
    pointed at a fake HTTP handler today.
- **`FallbackTranslator.cs`** tries providers in order. It catches `Exception ex when ex is not OperationCanceledException`.
  **Bug:** when `HttpClient.Timeout` expires, .NET throws `TaskCanceledException`, which is an
  `OperationCanceledException`. The timeout is therefore rethrown and is **not** passed to the next provider.
  `ChatPipeline.TranslateAsync` (`src/Envoy.Core/Pipeline/ChatPipeline.cs`) then reaches
  `catch (OperationCanceledException) { return; }` and never raises `MessageUpdated`. A slow Google endpoint leaves
  the overlay line on "translating…" for good, and Edge is never tried.
- **`CachingTranslator.cs`** is a 500-entry LRU in memory. The key is `source␟target␟trimmed text` and does not
  include the provider, so after switching to Claude the user is still served cached Google output. Nothing is kept
  between runs.
- **`UsageTracker.cs` / `BudgetedTranslator`** reserve characters **before** the call. Characters are counted even
  when the call never leaves the PC.
- **Concurrency.** `ChatPipeline.ProcessAsync` translates a whole OCR burst with `Task.WhenAll`, so 8 new lines send
  8 simultaneous requests to the keyless Google endpoint, with no limit. `ComposeWindow.TranslateNowAsync`
  (`src/Envoy.App/Windows/ComposeWindow.xaml.cs`) already cancels its previous request when the user keeps typing,
  but each keystroke pause still costs a full network call plus a back-translation.
- **Health.** `AppHost.BuildTranslator` turns `FallbackTranslator.ProviderFailed` into one string,
  `AppHost.ProviderStatus`. The Translation page shows it (`MainWindow.xaml` line ~248). It holds the last error only:
  no latency, no last success, no "skipping until …".
- **Tests.** `tests/Envoy.Core.Tests/TranslationTests.cs` (113 tests pass in the suite today) has parse tests for
  Google, Edge, DeepL and Claude and stack tests using a `FakeTranslator`. No test goes through HTTP status
  handling, headers, request shape, LibreTranslate parsing, or AWS.

### What is weak or missing for production

1. A timeout on the primary provider can hang a message forever, and the fallback is skipped (the bug above).
2. There are no retries for blips (one 502 means one failed line) and no `Retry-After` handling, which keeps
   hammering a provider that is rate-limiting.
3. A dead or blocked keyless endpoint is retried in full on every chat line. Each line waits up to 12 s before the
   fallback is tried.
4. No client-side throttling protects the unofficial free endpoints from bursts, and bursts are the usual way these
   endpoints get an IP blocked.
5. The same line seen twice in one OCR pass, or by the pipeline and the reply box at once, makes two identical calls.
6. The SDK retries inside Claude and AWS multiply with any outer retry. Their timeouts are not ours.
7. The UI cannot answer "is translation working, and which provider is in use?".
8. The cache dies on every settings edit and at exit, so paid users pay again for "gg wp" every match.
9. No tests pin the HTTP contract of each provider.

## Goals / Non-goals

**Goals**

- Every translation call ends within a bounded time, with a result or a categorised `TranslationException`. It
  never hangs, and a timeout is never silently dropped.
- Retry transient failures cheaply, respect `Retry-After`, and move to the next provider quickly when a provider is
  down, throttled or rejecting the key.
- Keep the keyless endpoints happy: bounded concurrency and a token-bucket throttle per provider.
- Make identical concurrent requests share one call, and do not run work that nobody waits for any more.
- Expose per-provider health (state, last success/failure, latency, retry time) for the Translation page and the
  War Room status line.
- Offer an opt-in, size-capped disk cache, and a memory cache that survives settings edits.
- Add contract tests for all six providers against recorded HTTP responses.

**Non-goals**

- Changing `ITranslator`, `TranslationRequest` or `TranslationResult`, or what `ChatPipeline` does with results.
  Pipeline behaviour belongs to `chat-understanding`.
- Adding or removing providers, or changing translation quality or prompts.
- Structured logging infrastructure (`reliability-observability`). This area raises a sanitised diagnostic event
  that area can subscribe to.
- Batching several lines into one provider request.
- Adding a package such as Polly or `System.Threading.RateLimiting`. The pieces are small, need `TimeProvider` for
  tests, and keep the dependency list short.

## Requirements

| ID | Pri | Requirement |
| --- | --- | --- |
| REQ-TRR-1 | MUST | `TranslationException` carries a `Kind` (`TranslationErrorKind`), an optional HTTP `StatusCode` and an optional `RetryAfter`, and every provider throws only `TranslationException` (or caller-requested `OperationCanceledException`) for HTTP errors, network errors, timeouts and malformed bodies. |
| REQ-TRR-2 | MUST | HTTP status codes are classified consistently: 408/500/502/503/504 (and Anthropic 529) → `Transient`; 429 → `RateLimited`; 401/403 on keyed providers → `Auth`; DeepL 456 and AWS `LimitExceededException` → `QuotaExceeded`; 400/404/413/414/422 → `Unsupported`; an unparseable 2xx body → `BadResponse`. |
| REQ-TRR-3 | MUST | `Retry-After` is parsed both as delta-seconds and as an HTTP-date, and is clamped to 0–3600 s. |
| REQ-TRR-4 | MUST | `Transient`, `Timeout` and `RateLimited` failures are retried up to the provider's `MaxAttempts`, with exponential backoff and full jitter (`delay = U(0,1) × min(MaxDelay, BaseDelay × 2^(attempt−1))`). `Auth`, `QuotaExceeded`, `BadResponse`, `Unsupported` and `Configuration` are never retried. |
| REQ-TRR-5 | MUST | When a 429/503 carries `Retry-After` ≤ `MaxRetryAfterWait` that also fits in the remaining deadline, the retry waits that long. A longer `Retry-After` is not waited for: the provider is marked `Throttled` until then and the request moves to the next provider. |
| REQ-TRR-6 | MUST | Each attempt has its own timeout (`AttemptTimeout`). Each top-level call has an overall deadline (`TranslationSettings.RequestTimeoutSeconds`, default 15 s) shared across retries and fallback providers. Hitting either produces `TranslationException(Kind = Timeout)`, not `OperationCanceledException`. |
| REQ-TRR-7 | MUST | `FallbackTranslator` moves to the next provider when a provider times out (including `TaskCanceledException` from `HttpClient.Timeout`) and rethrows `OperationCanceledException` only when the caller's token was cancelled. |
| REQ-TRR-8 | MUST | Each provider has a circuit breaker. After `BreakerThreshold` consecutive counted failures it opens for `BreakerOpenDuration`, which doubles after each failed half-open probe up to `BreakerMaxOpenDuration`. While open, calls fail in under 1 ms with `ProviderUnavailableException` and no network I/O. |
| REQ-TRR-9 | MUST | After the open period one half-open probe is let through. Concurrent requests are rejected while it runs. Success closes the breaker and resets the backoff. |
| REQ-TRR-10 | MUST | An `Auth` failure opens the breaker at once in state `AuthFailed` until the provider's credentials change. A `QuotaExceeded` failure opens it until `RetryAfter` or 1 hour. `Unsupported` failures and caller cancellations do not count towards the breaker. |
| REQ-TRR-11 | MUST | Breaker, throttle and health state for a provider survive `TranslatorFactory.Build` rebuilds. They reset only when that provider's configuration fingerprint (key, URL, model, region) changes or the user presses "Retry now". |
| REQ-TRR-12 | MUST | Each provider has a token-bucket throttle (defaults per provider in Design §4). A request waits at most `MaxQueueWait` for a token. Past that it fails with `ProviderUnavailableException` (`Throttled`, local) and moves to the next provider. |
| REQ-TRR-13 | MUST | Each provider has a concurrency limit (`MaxConcurrency`) and a waiting-queue limit (`MaxQueued`). A request beyond the queue limit fails fast with `ProviderUnavailableException`. |
| REQ-TRR-14 | MUST | Concurrent requests with the same cache key (cache namespace, source, target, trimmed text) share a single underlying call and all receive its result or exception. |
| REQ-TRR-15 | MUST | When a waiter cancels, only its own wait ends. The shared underlying call is cancelled when every waiter has cancelled. A request cancelled while queued for a slot, a token or a backoff delay makes no network call. |
| REQ-TRR-16 | MUST | `ProviderHealthRegistry` reports, for each provider: `State` (Unknown, Healthy, Degraded, Throttled, CoolingDown, AuthFailed, QuotaExceeded), last success and failure times, a sanitised last error, consecutive failures, median latency of the last 20 successes, success/failure counts and `RetryAt`. It raises `Changed` when any of these change. |
| REQ-TRR-17 | MUST | Health errors and diagnostic events never contain chat text, API keys or response bodies. They are built from provider name, kind, status code and a fixed message, cut to 160 characters, and any configured secret value is replaced with `***`. |
| REQ-TRR-18 | MUST | The Translation page shows one health row per provider in the active chain (name, coloured state dot from palette keys, state text, median latency, last success) with a "Retry now" action. `AppHost.ProviderStatus` is derived from the registry. |
| REQ-TRR-19 | MUST | The memory cache is owned by the long-lived `TranslationRuntime`, survives settings edits, and keys entries by a namespace made of the primary provider id plus the Claude model when Claude is primary. Switching the primary provider never serves the old provider's output. |
| REQ-TRR-20 | SHOULD | When `TranslationSettings.RememberTranslations` is on (default **off**), the cache is saved to `%APPDATA%\Envoy\translation-cache.json`. Saves are atomic (temp file + replace), run at most every 30 s while dirty, and also run at exit. Size is capped by `CacheMaxEntries` (default 2000) and 1 MiB, dropping least-recently-used entries. Entries are kept 30 days. Messages longer than 200 characters are not saved. |
| REQ-TRR-21 | SHOULD | Turning `RememberTranslations` off, or pressing "Clear saved translations", deletes the cache file and clears memory. A corrupt or unreadable file is renamed to `.bad` and the app starts with an empty cache. |
| REQ-TRR-22 | MUST | `BudgetedTranslator` gives back the reserved characters (`UsageTracker.Refund`) when the call is rejected locally (`ProviderUnavailableException`) or fails before a response, so local skips never use up the daily cap. Retries of one logical call reserve characters only once. |
| REQ-TRR-23 | MUST | The Claude and AWS SDK clients have their internal retries turned off (`MaxRetries = 0`, `MaxErrorRetry = 0`) and use the per-attempt timeout. Claude uses the injected `HttpClient`. |
| REQ-TRR-24 | MUST | Every provider has contract tests driven by a fake `HttpMessageHandler` and recorded response fixtures for success, 429 (with `Retry-After`), 500, malformed body and auth error (or the closest equivalent for keyless endpoints). They check the request shape (method, host/path, auth header, key fields) and the mapped result or `Kind`. |
| REQ-TRR-25 | SHOULD | `EdgeFreeTranslator` gets a fresh token and retries once, inside the same attempt, when the translate call returns 401. A second 401 is `RateLimited` (keyless endpoint refusing us), not `Auth`. |
| REQ-TRR-26 | SHOULD | `TranslationRuntime` raises a `Diagnostic` event (`TranslationDiagnostic` record: provider id, outcome, kind, status, attempt, latency) for every attempt, so the observability area can log it without parsing messages. |
| REQ-TRR-27 | COULD | When every provider in the chain is rejected locally (all breakers open or throttled), `FallbackTranslator` makes one real attempt on the provider with the earliest `RetryAt` that is not `AuthFailed`/`QuotaExceeded`, instead of failing without trying the network. |
| REQ-TRR-28 | SHOULD | `TranslationSettings.RequestTimeoutSeconds` is editable on the Translation page (5–30 s slider) and is clamped to 3–60 when loaded. |

## Design

### 1. Error model (`ITranslator.cs`, changed; `TranslationErrors.cs`, new)

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
    Configuration,  // missing key / URL
    Unavailable,    // rejected locally: breaker open, throttled, queue full
}

// Primary constructor gains optional parameters; existing `new TranslationException(msg)` / `(msg, inner)` calls compile unchanged.
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
    : TranslationException($"Daily character limit …", kind: TranslationErrorKind.QuotaExceeded) { … }   // text unchanged

public sealed class ProviderUnavailableException(string providerId, ProviderState state, DateTimeOffset? retryAt, string message)
    : TranslationException(message, kind: TranslationErrorKind.Unavailable)
{
    public string ProviderId { get; } = providerId;
    public ProviderState State { get; } = state;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
```

Missing-credential exceptions in the providers pass `kind: Configuration`.

### 2. Shared HTTP helper (`Providers/ProviderHttp.cs`, new, `internal static`)

```csharp
internal static class ProviderHttp
{
    /// Throws a classified TranslationException for non-2xx responses (status overrides per provider), else returns the body.
    public static Task<string> ReadBodyAsync(HttpResponseMessage response, string providerName, TimeProvider clock,
        Func<int, TranslationErrorKind?>? overrideKind, CancellationToken ct);

    public static TranslationErrorKind Classify(int status);            // REQ-TRR-2 table
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset now); // REQ-TRR-3

    /// Runs a parser; JsonException/KeyNotFound/IndexOutOfRange/InvalidOperation/FormatException → BadResponse.
    public static TranslationResult Parse(string providerName, string body, Func<string, TranslationResult> parse);

    /// Message text: "<Provider> returned 429 (rate limited)." — never includes the body.
    public static string Describe(string providerName, int status, TranslationErrorKind kind);
}
```

Changes for each provider. Request URLs and bodies stay byte-for-byte the same. Every `Parse` call goes through
`ProviderHttp.Parse`.

| Provider | Changes |
| --- | --- |
| `GoogleFreeTranslator` | `ReadBodyAsync` with override 403 → `RateLimited`. A 2xx body that is not a JSON array → `BadResponse`. |
| `EdgeFreeTranslator` | Override 401 → refresh the token and resend once (REQ-TRR-25); a second 401 → `RateLimited`. A failing auth endpoint → `Transient`. Constructor unchanged. |
| `DeepLTranslator` | Overrides 403 → `Auth` ("DeepL rejected the API key."), 456 → `QuotaExceeded` ("DeepL monthly quota used up."). Existing messages kept. |
| `LibreTranslateTranslator` | `Parse` moves to an `internal static Parse(string json, string providerId)` so it can be tested. 403 → `Auth`, 400 → `Unsupported`. |
| `ClaudeTranslator` | New constructor `ClaudeTranslator(string apiKey, string model, HttpClient? http = null, TimeSpan? timeout = null)`. It sets `AnthropicClient { HttpClient = http, MaxRetries = 0, Timeout = timeout ?? 12 s }`. Exception mapping: `AnthropicRateLimitException` → `RateLimited`; `AnthropicUnauthorizedException`/`AnthropicForbiddenException` → `Auth`; `Anthropic5xxException` and status 529 → `Transient`; `AnthropicIOException` → `Transient`; `AnthropicBadRequestException`/`AnthropicUnprocessableEntityException` → `Unsupported`; `AnthropicInvalidDataException` → `BadResponse`. `RetryAfter` is read from the exception's response headers if the SDK exposes them (open question 2); otherwise it is null and backoff applies. The refusal path stays `Unsupported`. |
| `AwsTranslator` | New optional constructor parameter `Amazon.Runtime.HttpClientFactory? httpClientFactory = null` (tests only) and `TimeSpan? timeout`. It sets `AmazonTranslateConfig { MaxErrorRetry = 0, Timeout = …, HttpClientFactory = … }`. Mapping by `ErrorCode`: `ThrottlingException`/`TooManyRequestsException` → `RateLimited`; `UnrecognizedClientException`/`InvalidSignatureException`/`AccessDeniedException`/`ExpiredTokenException` → `Auth`; `LimitExceededException` → `QuotaExceeded`; `UnsupportedLanguagePairException`/`TextSizeLimitExceededException`/`DetectedLanguageLowConfidenceException` → `Unsupported`; `ServiceUnavailableException`/`InternalServerException`/5xx → `Transient`; `AmazonClientException` (network) → `Transient`. |

`TranslatorFactory.Create` passes the shared `HttpClient` to Claude, and the per-provider `AttemptTimeout` to Claude
and AWS.

### 3. Resilience building blocks (`src/Envoy.Core/Translation/Resilience/`, new)

All of them take a `TimeProvider` (production: `TimeProvider.System`; tests: `FakeTimeProvider`). All waits use
`Task.Delay(TimeSpan, TimeProvider, CancellationToken)` and `new CancellationTokenSource(TimeSpan, TimeProvider)`
(both in .NET 8).

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
    public int MaxConcurrency { get; init; } = 4;
    public int MaxQueued { get; init; } = 16;

    public static ResilienceOptions For(string providerId);         // table in §4
}

public static class RetryPolicy
{
    public static bool IsRetryable(TranslationErrorKind kind);       // Transient, Timeout, RateLimited
    public static TimeSpan Backoff(int attempt, ResilienceOptions o, double jitter01);   // full jitter, attempt is 1-based
    /// Returns the delay before the next attempt, or null when the caller should stop and fall through.
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
    public void OnFailure(TranslationException error); // applies REQ-TRR-8/10 rules
    public void Reset();
}

public sealed class TokenBucket(double tokensPerSecond, int burst, TimeProvider clock)
{
    public bool TryTake();
    /// Waits up to maxWait for a token; false if none became available in time.
    public Task<bool> TakeAsync(TimeSpan maxWait, CancellationToken ct);
}
```

**`ProviderGuard`** holds all the state for one provider: breaker, bucket, `SemaphoreSlim` gate, queued count,
`ProviderHealth`, options and configuration fingerprint. It lives in `TranslationRuntime`, keyed by provider id.

**`ResilientTranslator(ITranslator inner, ProviderGuard guard, TimeProvider clock, Func<double>? jitter = null) : ITranslator`**
handles one logical call:

1. `guard.Breaker.TryEnter` fails → throw `ProviderUnavailableException` (state from `OpenReason`, `RetryAt = OpenUntil`).
2. Queue full → `ProviderUnavailableException("… is busy")`. Otherwise wait on the gate, bounded by the remaining
   deadline and the caller's token.
3. `TokenBucket.TakeAsync(MaxQueueWait)` returns false → `ProviderUnavailableException(Throttled)`.
4. Attempt loop: a linked CTS of the caller token plus an attempt timeout of `min(AttemptTimeout, remaining deadline)`.
   It calls `inner`. An `OperationCanceledException` when the caller's token is **not** cancelled →
   `TranslationException(Timeout)`. `HttpRequestException`/`IOException` → `Transient`. Any other
   non-`TranslationException` → `Unknown`. On failure, `RetryPolicy.NextDelay` decides between waiting and retrying
   or rethrowing.
5. Records the result in health and the breaker: success with latency, or failure with kind. Raises
   `TranslationRuntime.Diagnostic`. Releases the gate in `finally`. A half-open probe that is cancelled by the
   caller returns the breaker to Open with the same `OpenUntil` (it is not counted as a failure).

**`TranslationDeadline`** (internal, `AsyncLocal<DateTimeOffset?>`). `FallbackTranslator` sets it for the length of
a call so `ResilientTranslator` can tell how much time is left (REQ-TRR-5/6) without changing `ITranslator`.

**`CoalescingTranslator(ITranslator inner, Func<TranslationRequest, string> key) : ITranslator`** keeps a
`Dictionary<string, Flight>` under a lock. A `Flight` holds a shared `Task<TranslationResult>`, its own
`CancellationTokenSource` and a waiter count. Each caller awaits `flight.Task.WaitAsync(callerToken)`. When a caller
cancels, the count goes down, and at zero the flight's CTS is cancelled. The entry is removed when the task
completes. Failures are shared too, so a burst of identical lines makes one failed call.

### 4. Default policy per provider (`ResilienceOptions.For`)

These are conservative client-side defaults. They are **not** the providers' published limits, which are unknown
for the keyless endpoints. They are constants in code (not user settings), so they can be tuned after release
without a settings migration.

| Provider | AttemptTimeout | MaxAttempts | Throttle (tokens/s, burst) | MaxConcurrency | BreakerThreshold / open |
| --- | --- | --- | --- | --- | --- |
| `google-free` | 5 s | 2 | 2/s, 6 | 3 | 3 / 20 s → 5 min |
| `edge-free` | 5 s | 2 | 2/s, 6 | 3 | 3 / 20 s → 5 min |
| `deepl` | 6 s | 3 | none | 4 | 5 / 20 s → 5 min |
| `claude` | 12 s | 2 | none | 2 | 3 / 20 s → 5 min |
| `aws` | 6 s | 3 | none | 4 | 5 / 20 s → 5 min |
| `libretranslate` | 8 s | 2 | none | 2 | 3 / 10 s → 2 min |

### 5. Health (`Resilience/ProviderHealth.cs`, new)

```csharp
public enum ProviderState { Unknown, Healthy, Degraded, Throttled, CoolingDown, AuthFailed, QuotaExceeded }

public sealed record ProviderHealthSnapshot(
    string ProviderId, ProviderState State,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure,
    string? LastError, TranslationErrorKind? LastErrorKind,
    int ConsecutiveFailures, TimeSpan? MedianLatency,
    DateTimeOffset? RetryAt, long Successes, long Failures);

public sealed class ProviderHealthRegistry(TimeProvider clock)
{
    public ProviderHealthSnapshot Get(string providerId);
    public IReadOnlyList<ProviderHealthSnapshot> Snapshot(IEnumerable<string> providerIds);
    public event Action<string>? Changed;               // provider id; raised on a thread-pool thread
}

public static class ProviderHealthText
{
    /// "Healthy · 340 ms", "Skipping for 18 s — rate limited", "Key rejected — check it in Settings", "Daily cap reached"
    public static string Describe(ProviderHealthSnapshot s, DateTimeOffset now);
    /// War Room line: "Using Google Translate (free) · healthy" / "Google unavailable — using Microsoft Translator (free)".
    public static string Summary(IReadOnlyList<ProviderHealthSnapshot> chain, DateTimeOffset now);
    public static string Sanitize(string message, IEnumerable<string> secrets);   // REQ-TRR-17
}
```

`Degraded` means the breaker is closed but there are consecutive failures > 0, or the last success needed a retry.
`BudgetedTranslator` takes an optional `ProviderHealth` and marks `QuotaExceeded` with `RetryAt` = next local
midnight when it throws `BudgetExceededException`.

### 6. Cache (`TranslationCache.cs`, `TranslationCacheStore.cs`, new; `CachingTranslator.cs`, changed)

```csharp
public sealed class TranslationCache(int capacity, TimeProvider clock)
{
    public bool TryGet(string key, out TranslationResult result);
    public void Set(string key, TranslationResult result);
    public int Count { get; }
    public bool IsDirty { get; }
    public void Clear();
    public IReadOnlyList<CacheEntry> Export();          // LRU order, most recent first
    public void Import(IEnumerable<CacheEntry> entries); // drops expired (> 30 days)
    public static string Key(string ns, TranslationRequest r); // ns␟source␟target␟trimmed text
}

public sealed record CacheEntry(string KeyHash, string Text, string? DetectedSourceLanguage, string ProviderId, DateTimeOffset At);

public sealed class TranslationCacheStore(string path)
{
    public IReadOnlyList<CacheEntry> Load();                    // corrupt → rename to .bad, return []
    public void Save(IReadOnlyList<CacheEntry> entries, int maxBytes = 1 << 20); // temp + File.Move(overwrite)
    public void Delete();
}

// Existing constructor kept for tests/back-compat (private cache, namespace "").
public sealed class CachingTranslator(ITranslator inner, TranslationCache cache, string cacheNamespace) : ITranslator
{
    public CachingTranslator(ITranslator inner, int capacity = 500);
}
```

In memory the key is the plain composite key. On disk only `KeyHash` (SHA-256 hex of the key) is stored, so the
file holds translations but not the original lines. File format:
`{"version":1,"entries":[{"k":"…","t":"…","d":"zh-CN","p":"google-free","at":"2026-10-14T…Z"}]}`.
`FromCache` is set on hits as it is today. Results flagged `FromGlossary` and results for requests longer than
200 characters are not persisted.

### 7. Runtime and composition (`TranslationRuntime.cs`, new; `TranslatorFactory.cs`, `FallbackTranslator.cs`, `UsageTracker.cs`, changed)

```csharp
/// Long-lived translation state owned by the app; survives TranslatorFactory rebuilds.
public sealed class TranslationRuntime : IDisposable
{
    public TranslationRuntime(TimeProvider? clock = null, string? cachePath = null);
    public ProviderHealthRegistry Health { get; }
    public TranslationCache Cache { get; }
    public event Action<TranslationDiagnostic>? Diagnostic;

    public void Configure(TranslationSettings settings);   // enables/disables disk cache (deletes file when turned off)
    public void ResetProvider(string providerId);          // "Retry now": breaker + throttle + health reset
    public void ClearCache();                              // memory + file
    public void Flush();                                   // synchronous save if enabled and dirty
    public void Dispose();                                 // Flush + stop timer
    internal ProviderGuard Guard(string providerId, string configFingerprint);
}

public sealed record TranslationDiagnostic(string ProviderId, string Outcome, TranslationErrorKind? Kind,
                                           int? StatusCode, int Attempt, TimeSpan Elapsed);
```

`TranslatorFactory(HttpClient http, UsageTracker usage, TranslationRuntime? runtime = null)`. The existing
two-argument use still works and creates a private runtime. `Build` produces:

```
Caching(runtime.Cache, ns) → Coalescing → Fallback(deadline = RequestTimeoutSeconds)[
    Budgeted( Resilient( provider, runtime.Guard(id, fingerprint) ) ) …, Budgeted(Resilient(GoogleFree)) ]
```

The configuration fingerprint is the SHA-256 of the provider's credential and endpoint fields. It is computed in
memory and never stored or logged.

`FallbackTranslator(IReadOnlyList<ITranslator> chain, TimeSpan? deadline = null, TimeProvider? clock = null)`:

- Sets `TranslationDeadline` and links a deadline CTS.
- Catches `OperationCanceledException` when the **caller's** token is not cancelled and treats it as `Timeout`
  (REQ-TRR-7).
- Skips `ProviderUnavailableException` without raising `ProviderFailed`, since the skip is already shown in health.
- When the deadline fires, throws `TranslationException(Timeout, "No translation within 15 s")`.
- The combined failure message lists provider names and kinds only.
- COULD (REQ-TRR-27): forced probe when everything was rejected locally.

`UsageTracker.Refund(string providerId, int characters)` (new, never goes below 0). `BudgetedTranslator` calls it on
`ProviderUnavailableException`, and on `Transient`/`Timeout` failures that carry no `StatusCode` (no response was
received).

### 8. Settings added (`AppSettings.cs` → `TranslationSettings`, shared file)

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `RequestTimeoutSeconds` | `int` | `15` | Overall deadline per translation. Clamped to 3–60 on use. UI slider 5–30. |
| `RememberTranslations` | `bool` | `false` | Opt-in disk cache (privacy: off like `SaveTranscripts`). |
| `CacheMaxEntries` | `int` | `2000` | Memory and disk cap. Clamped to 100–20000. Not shown in the UI. |

No migration is needed: missing properties deserialise to their defaults.

### 9. App changes (thin shell)

**`AppHost.cs` (shared):**

- Add the field and property `public TranslationRuntime TranslationRuntime { get; }`, created before `_factory` with
  `cachePath: Path.Combine(DataFolder, "translation-cache.json")`.
- Change to `_factory = new TranslatorFactory(_http, Usage, TranslationRuntime)`.
- `ApplySettings()` calls `TranslationRuntime.Configure(Settings.Translation)` before `BuildTranslator()`.
- `BuildTranslator()` keeps the `ProviderFailed` callback for credential errors only. `_providerStatus` comes from
  `ProviderHealthText.Summary(...)` over the active chain ids.
- Subscribe to `TranslationRuntime.Health.Changed` and, with at most one pending refresh,
  `_ui.BeginInvoke(() => Notify(nameof(ProviderStatus)); ProviderHealthChanged?.Invoke())`. New
  `public event Action? ProviderHealthChanged;` and `public IReadOnlyList<string> ActiveProviderIds`.
- `Dispose()`/exit calls `TranslationRuntime.Dispose()` (flush).
- SHOULD: raise `HttpClient.Timeout` to 30 s, because it is only a backstop now. Core timeouts are shorter.

**Translation page** (`src/Envoy.App/Pages/TranslationPage.xaml` once `01-ui-foundation` lands; until then the
Translation section of `MainWindow.xaml`), plus `ViewModels/MainViewModel.Translation.cs`:

- **"Provider health" card** under the provider pickers: an `ItemsControl` bound to
  `ObservableCollection<ProviderHealthRow> ProviderHealth`. `ProviderHealthRow` (in `ViewModels/`) has `Name`,
  `StateText` (from `ProviderHealthText.Describe`), `LastSuccessText` and `State`. A `DataTrigger` on `State` picks
  the dot brush from the existing palette keys only: Healthy → `Success`; Degraded/Throttled/CoolingDown → `Accent`;
  AuthFailed/QuotaExceeded → `Danger`; Unknown → `Text.Muted`. A "Retry now" button bound to `RetryProviderCommand`
  (parameter: provider id) calls `TranslationRuntime.ResetProvider`. The rows refresh on `Host.ProviderHealthChanged`
  and on a 1 s `DispatcherTimer` while the page is visible, so countdowns update.
- **"Remember translations between sessions" checkbox** bound to `Settings.Translation.RememberTranslations`, with a
  caption `"{Cache.Count:N0} translations saved on this PC only"` and a "Clear saved translations" button
  (`ClearTranslationCacheCommand`).
- **"Give up after" slider** (5–30 s) bound to `Settings.Translation.RequestTimeoutSeconds`.
- No new palette keys. All brushes are `DynamicResource`.

**`SmokeTest.cs` (shared):** before the theme loop, seed health with one provider in each state using
`TranslationRuntime.Health` test hooks (`internal void Seed(ProviderHealthSnapshot)`, exposed through
`InternalsVisibleTo("Envoy")`, or a public `ProviderHealthRegistry.Seed` marked for diagnostics). That way the
health card renders every dot colour under every theme. The existing `Translation` screenshot then shows the card.

### Threading

- Core types are thread-safe: each guard has one lock for the breaker and bucket, `SemaphoreSlim` for concurrency,
  a lock-protected dictionary in `CoalescingTranslator` and `TranslationCache`, and `Interlocked` counters in health.
  No lock is held across an `await`.
- `Health.Changed` and `Diagnostic` fire on whichever thread finished the attempt. `AppHost` marshals them to the UI
  dispatcher and coalesces them.
- The disk cache loads synchronously in the `TranslationRuntime` constructor (≤ 1 MiB) and saves from a
  `TimeProvider.CreateTimer` callback on the thread pool, with a `SemaphoreSlim(1)` so it never overlaps `Flush()`
  at exit.

### Failure behaviour

| Situation | Behaviour |
| --- | --- |
| Primary provider times out | Timeout after `AttemptTimeout`, one retry if the deadline allows, then the next provider. The message never stays Pending. |
| Google returns 429 with `Retry-After: 120` | No wait. Google is `Throttled` for 120 s, Edge answers. Google gets no calls until the half-open probe after 120 s. |
| Endpoint returns HTML | `BadResponse` and fall through. The health row reads "Unexpected reply from Google Translate (free)". The message does not show a JSON parser error. |
| Wrong DeepL key | One call: `Auth` → `AuthFailed`. The breaker stays open until the key changes or "Retry now" is pressed. The health row is red ("Key rejected"). Fallback serves chat. Characters are refunded. |
| Every provider down | Each message fails within the deadline with "Translation unavailable: Google (cooling down 14 s), Microsoft (rate limited)". The overlay shows the error as today. |
| Disk cache corrupt | Renamed to `translation-cache.json.bad`, empty cache, no crash. |
| Disk full or file locked on save | Save skipped, cache stays dirty, retried on the next tick. |

## Acceptance criteria

| ID | Requirements | Verified by |
| --- | --- | --- |
| AC-TRR-1 | REQ-TRR-1, REQ-TRR-2, REQ-TRR-3 | Unit tests `ProviderHttpTests.Classify_maps_status_codes` (Theory over 400/401/403/404/408/413/414/422/429/456/500/502/503/504/529), `ParseRetryAfter_accepts_seconds_and_http_date`, `ParseRetryAfter_clamps_to_one_hour`, `Parse_wraps_json_errors_as_BadResponse`. |
| AC-TRR-2 | REQ-TRR-4 | `RetryPolicyTests.Backoff_is_full_jitter_and_capped`, `Never_retries_auth_quota_badresponse_unsupported`, `ResilientTranslatorTests.Retries_transient_until_MaxAttempts_then_throws`. |
| AC-TRR-3 | REQ-TRR-5 | `ResilientTranslatorTests.Waits_short_Retry_After_then_succeeds`, `Long_Retry_After_marks_throttled_and_throws_without_waiting`. |
| AC-TRR-4 | REQ-TRR-6, REQ-TRR-7 | `ResilientTranslatorTests.Attempt_timeout_becomes_Timeout_kind`, `FallbackDeadlineTests.HttpClient_timeout_falls_through_to_next_provider`, `Overall_deadline_throws_Timeout_not_OperationCanceled`, `Caller_cancellation_still_throws_OperationCanceled`. |
| AC-TRR-5 | REQ-TRR-7 (end to end) | `FallbackDeadlineTests.Pipeline_marks_message_failed_when_all_providers_time_out`: a `ChatPipeline` over a factory-built stack whose fake handler never answers ends in `IncomingStatus.Failed` (not Pending) after advancing the fake clock past the deadline. |
| AC-TRR-6 | REQ-TRR-8, REQ-TRR-9 | `CircuitBreakerTests.Opens_after_threshold_consecutive_failures`, `Rejects_while_open_without_calling_inner`, `Allows_single_half_open_probe`, `Probe_success_closes_and_resets_backoff`, `Probe_failure_doubles_open_duration_up_to_max`. |
| AC-TRR-7 | REQ-TRR-10 | `CircuitBreakerTests.Auth_opens_immediately_until_reset`, `Quota_opens_until_retry_after_or_one_hour`, `Unsupported_and_cancellation_do_not_count`. |
| AC-TRR-8 | REQ-TRR-11 | `TranslationRuntimeTests.Breaker_state_survives_factory_rebuild`, `Fingerprint_change_resets_provider`, `ResetProvider_closes_breaker_and_clears_health`. |
| AC-TRR-9 | REQ-TRR-12 | `TokenBucketTests.Burst_then_refills_at_rate`, `TakeAsync_gives_up_after_max_wait`, `ResilientTranslatorTests.Throttled_request_falls_through_without_network_call`. |
| AC-TRR-10 | REQ-TRR-13 | `ResilientTranslatorTests.Never_exceeds_MaxConcurrency` (gated fake counts peak in-flight), `Queue_overflow_fails_fast`. |
| AC-TRR-11 | REQ-TRR-14, REQ-TRR-15 | `CoalescingTranslatorTests.Identical_concurrent_requests_make_one_call`, `Different_keys_are_not_coalesced`, `One_waiter_cancelling_does_not_cancel_others`, `All_waiters_cancelling_cancels_inner_call`, `Failure_is_shared_and_entry_removed`; `ResilientTranslatorTests.Cancelled_while_queued_makes_no_call`. |
| AC-TRR-12 | REQ-TRR-16 | `ProviderHealthTests.Records_success_latency_median_of_last_20`, `State_transitions_follow_breaker`, `Changed_raised_on_state_change`; `ProviderHealthTextTests.Describe_formats_each_state` (Theory). |
| AC-TRR-13 | REQ-TRR-17 | `ProviderHealthTests.Last_error_never_contains_request_text_or_key`: a fake handler returning a body that echoes the text and key leaves `LastError`, `TranslationDiagnostic` and the `FallbackTranslator` aggregate message free of both strings. |
| AC-TRR-14 | REQ-TRR-18, REQ-TRR-28 | `tools/typecheck/typecheck.sh` passes, with resource keys `Success`/`Accent`/`Danger`/`Text.Muted` resolved. The Windows CI smoke test opens the Translation page with seeded health under all 5 themes. The `{theme}-settings-translation.png` screenshots show the health card. Manual step M1. |
| AC-TRR-15 | REQ-TRR-19 | `TranslationCacheTests.Cache_survives_rebuild_in_runtime`, `Switching_primary_provider_misses_cache`. |
| AC-TRR-16 | REQ-TRR-20, REQ-TRR-21 | `TranslationCacheStoreTests.Roundtrip_preserves_entries_and_lru_order`, `Save_is_atomic_and_capped_at_max_bytes`, `Expired_entries_dropped_on_import`, `Long_messages_not_persisted`, `Corrupt_file_renamed_to_bad`, `Disk_file_contains_only_key_hashes`; `TranslationRuntimeTests.Disabling_remember_deletes_file`, `Flush_on_dispose_writes_when_dirty`. Manual step M3. |
| AC-TRR-17 | REQ-TRR-22 | `BudgetTests.Local_rejection_refunds_characters`, `Retries_reserve_characters_once`. |
| AC-TRR-18 | REQ-TRR-23, REQ-TRR-24 | `ProviderContractTests` (one Theory per provider × scenario, see the test plan). The Claude test asserts exactly 1 request reaches the handler on a 500 when called directly (SDK retries off). |
| AC-TRR-19 | REQ-TRR-25 | `ProviderContractTests.Edge_refreshes_token_once_on_401`, `Edge_second_401_is_RateLimited`. |
| AC-TRR-20 | REQ-TRR-26 | `TranslationRuntimeTests.Diagnostic_raised_per_attempt_with_kind_and_status`. |
| AC-TRR-21 | REQ-TRR-27 | `FallbackDeadlineTests.All_open_forces_single_probe_on_earliest_retry` (only if REQ-TRR-27 is implemented). |
| AC-TRR-22 | all | `dotnet test tests/Envoy.Core.Tests` is green on Linux and in CI. Existing `TranslatorStackTests` still pass unchanged (back-compat constructors). |

## Test plan

New test files go in `tests/Envoy.Core.Tests/Translation/`, all xUnit, with `FakeTimeProvider` from
`Microsoft.Extensions.TimeProvider.Testing`, which is already referenced. No test touches the real network or waits
on real time.

**Helpers**

- `Fakes/FakeHttpHandler.cs`: a `HttpMessageHandler` that records every `HttpRequestMessage` (method, URI, headers,
  body string) and answers from a queue of `Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>`.
  Helpers: `Respond(fixture)`, `Hang()` (awaits `Task.Delay(Infinite, ct)`), `Throw(new HttpRequestException())`.
- `Fakes/ProviderFixture.cs`: loads `Fixtures/Providers/<provider>/<scenario>.json`, with the shape
  `{ "status": 429, "headers": {"Retry-After": "2"}, "contentType": "application/json", "body": "…" }`.
- `Fakes/ScriptedTranslator.cs`: an `ITranslator` with a programmable sequence of results or exceptions, an optional
  gate (`TaskCompletionSource`) and an in-flight counter. The existing `FakeTranslator` is kept.
- Fixtures under `tests/Envoy.Core.Tests/Fixtures/Providers/` for `google-free`, `edge-free` (plus `auth-token.txt`),
  `deepl`, `libretranslate`, `claude` and `aws`, each with `success`, `429`, `500`, `malformed` and `auth` (Google:
  `403`; Edge: `401`). The csproj adds `<None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />`.
  `Fixtures/README.md` records where each body came from.

**Unit tests**

| File | Test | Asserts |
| --- | --- | --- |
| `ProviderHttpTests.cs` | `Classify_maps_status_codes` | REQ-TRR-2 table, each status → kind |
| | `ParseRetryAfter_accepts_seconds_and_http_date` | `"2"` → 2 s; an RFC 1123 date 30 s ahead → 30 s |
| | `ParseRetryAfter_clamps_to_one_hour` | `"99999"` → 3600 s; a past date → 0 |
| | `Parse_wraps_json_errors_as_BadResponse` | HTML body, `{}`, `[]` → `BadResponse`, message has no body text |
| `RetryPolicyTests.cs` | `Backoff_is_full_jitter_and_capped` | jitter 0 → 0; jitter 1 at attempt 1/2/5 → 250/500/2000 ms (capped) |
| | `Never_retries_auth_quota_badresponse_unsupported` | `NextDelay` returns null for those kinds |
| | `Retry_after_beyond_deadline_returns_null` | remaining 1 s, Retry-After 2 s → null |
| `CircuitBreakerTests.cs` | `Opens_after_threshold_consecutive_failures` | 2 failures Closed, 3rd → Open, `OpenUntil = now + 20 s` |
| | `Success_resets_consecutive_count` | F, F, S, F, F → still Closed |
| | `Rejects_while_open_without_calling_inner` | via `ResilientTranslator` + `ScriptedTranslator`: 0 inner calls, < 1 ms |
| | `Allows_single_half_open_probe` | after advancing 20 s, first `TryEnter` is a probe, second is rejected |
| | `Probe_success_closes_and_resets_backoff` | Closed; next open is 20 s again |
| | `Probe_failure_doubles_open_duration_up_to_max` | 20 → 40 → 80 … capped at 5 min |
| | `Auth_opens_immediately_until_reset` | one `Auth` → `AuthFailed`; time +1 day still open; `Reset()` closes |
| | `Quota_opens_until_retry_after_or_one_hour` | with and without `RetryAfter` |
| | `Unsupported_and_cancellation_do_not_count` | 10 `Unsupported` → Closed |
| `TokenBucketTests.cs` | `Burst_then_refills_at_rate` | 6 takes succeed, 7th fails; +500 ms → 1 more |
| | `TakeAsync_gives_up_after_max_wait` | empty bucket at 0.1/s, maxWait 1.5 s → false after the clock advances 1.5 s |
| | `TakeAsync_honours_cancellation` | throws `OperationCanceledException`, token not consumed |
| `ResilientTranslatorTests.cs` | `Retries_transient_until_MaxAttempts_then_throws` | 2 calls, final `Transient` |
| | `Succeeds_on_retry_and_reports_degraded` | 500 then OK → result, health `Degraded` |
| | `Waits_short_Retry_After_then_succeeds` | 429 `Retry-After: 1` → second call only after the clock advances 1 s |
| | `Long_Retry_After_marks_throttled_and_throws_without_waiting` | 429 `Retry-After: 120` → immediate throw, state `Throttled`, `RetryAt = +120 s` |
| | `Attempt_timeout_becomes_Timeout_kind` | hanging inner, advance 5 s → `TranslationException(Timeout)` |
| | `Throttled_request_falls_through_without_network_call` | `ProviderUnavailableException(Throttled)`; handler count unchanged |
| | `Never_exceeds_MaxConcurrency` | 10 parallel calls, peak in-flight == 3 for google-free |
| | `Queue_overflow_fails_fast` | MaxQueued + MaxConcurrency + 1 → last throws immediately |
| | `Cancelled_while_queued_makes_no_call` | cancel a waiter behind the gate → inner never sees it |
| | `Non_translation_exceptions_are_wrapped` | `HttpRequestException` → `Transient`; `InvalidOperationException` → `Unknown` |
| `CoalescingTranslatorTests.cs` | `Identical_concurrent_requests_make_one_call` | 5 identical → inner count 1, 5 equal results |
| | `Different_keys_are_not_coalesced` | different target → 2 calls |
| | `One_waiter_cancelling_does_not_cancel_others` | A cancels, B still gets the result, inner token not cancelled |
| | `All_waiters_cancelling_cancels_inner_call` | inner observes cancellation |
| | `Failure_is_shared_and_entry_removed` | both get the exception; the next call makes a new inner call |
| `FallbackDeadlineTests.cs` | `HttpClient_timeout_falls_through_to_next_provider` | first provider throws `TaskCanceledException` (caller token live) → second answers |
| | `Caller_cancellation_still_throws_OperationCanceled` | |
| | `Overall_deadline_throws_Timeout_not_OperationCanceled` | two hanging providers, deadline 15 s → `Timeout` |
| | `Unavailable_providers_skipped_without_ProviderFailed_event` | |
| | `Pipeline_marks_message_failed_when_all_providers_time_out` | see AC-TRR-5 |
| | `All_open_forces_single_probe_on_earliest_retry` | COULD |
| `ProviderHealthTests.cs` | `Records_success_latency_median_of_last_20` | 25 samples → median of the last 20 |
| | `State_transitions_follow_breaker` | Healthy → Degraded → CoolingDown → Healthy |
| | `Changed_raised_on_state_change` | |
| | `Last_error_never_contains_request_text_or_key` | AC-TRR-13 |
| | `Budget_exceeded_marks_quota_until_midnight` | |
| `ProviderHealthTextTests.cs` | `Describe_formats_each_state` | one row per `ProviderState` |
| | `Summary_names_active_provider_when_primary_unavailable` | |
| `TranslationCacheTests.cs` | `Lru_evicts_oldest_at_capacity` | |
| | `Cache_survives_rebuild_in_runtime` | build, translate, build again, translate → `FromCache` |
| | `Switching_primary_provider_misses_cache` | |
| `TranslationCacheStoreTests.cs` | `Roundtrip_preserves_entries_and_lru_order` | temp dir |
| | `Save_is_atomic_and_capped_at_max_bytes` | no `.tmp` left, size ≤ cap, most recent kept |
| | `Expired_entries_dropped_on_import` | 31-day-old entry gone |
| | `Long_messages_not_persisted` | 201-char request not in file |
| | `Corrupt_file_renamed_to_bad` | |
| | `Disk_file_contains_only_key_hashes` | source text absent from the file bytes |
| `TranslationRuntimeTests.cs` | `Breaker_state_survives_factory_rebuild` | |
| | `Fingerprint_change_resets_provider` | change `DeepLKey` → breaker Closed |
| | `ResetProvider_closes_breaker_and_clears_health` | |
| | `Disabling_remember_deletes_file` | |
| | `Flush_on_dispose_writes_when_dirty` | |
| | `Diagnostic_raised_per_attempt_with_kind_and_status` | |
| `BudgetTests.cs` | `Local_rejection_refunds_characters` | open breaker → `UsageTracker.Today` unchanged |
| | `Retries_reserve_characters_once` | 500 then OK → usage == text length |
| `ProviderContractTests.cs` | `Google_contract(scenario)` | GET `translate.googleapis.com/translate_a/single`, `client=gtx`, `tl`, `q` escaped; success parse; 429 → `RateLimited` + `RetryAfter`; 500 → `Transient`; HTML → `BadResponse`; 403 → `RateLimited` |
| | `Edge_contract(scenario)` | GET auth then POST `api-edge…/translate?api-version=3.0&to=…`, `Authorization: Bearer <token>`; scenarios as above |
| | `Edge_refreshes_token_once_on_401`, `Edge_second_401_is_RateLimited` | |
| | `DeepL_contract(scenario)` | `:fx` key → `api-free.deepl.com`, `Authorization: DeepL-Auth-Key …`, body `text[]`, `target_lang`; 403 → `Auth`; 456 → `QuotaExceeded` |
| | `Libre_contract(scenario)` | POST `{base}/translate`, body `q/source/target/format/api_key`; 403 → `Auth`; 400 → `Unsupported` |
| | `Claude_contract(scenario)` | POST `/v1/messages`, `x-api-key` header, model and system present; `zh-CN|…` parsed; 429/500/529/401 kinds; exactly one request per call |
| | `Aws_contract(scenario)` (SHOULD) | via test `HttpClientFactory`; `X-Amz-Target: AWSShineFrontendService_20170701.TranslateText`; error codes → kinds; one request per call |

**Smoke-test additions** (`SmokeTest.cs`): seed `TranslationRuntime.Health` with Healthy, Degraded, Throttled,
AuthFailed and Unknown providers before the theme loop. While on `Translation`, assert the health `ItemsControl` has
≥ 1 generated container and log `"health rows: N"`. A missing resource or binding fails the run as today.

**Manual checks (Windows)**

- **M1.** Disconnect the network, type in the Translation test box, and press Translate. Within 15 s you see a
  failure, not an endless "…". The health card shows Google and Microsoft "Cooling down". Reconnect and press
  "Retry now". The row turns green on the next translation.
- **M2.** Put an invalid DeepL key as primary. The first message is translated by the fallback. The DeepL row is red
  "Key rejected". Fix the key, and the row returns to Unknown, then Healthy.
- **M3.** Turn on "Remember translations", translate "gg wp" in the test box, quit, and restart. Translating again
  shows the result instantly. The file `%APPDATA%\Envoy\translation-cache.json` exists and does not contain
  "gg wp" in plain text. Turn the option off, and the file is gone.
- **M4.** In a live match, or with the demo feed, send a burst of ≥ 8 chat lines. All of them are translated, and
  the War Room status never shows Google as throttled from the burst alone.

## Files touched

| Path | Change | Shared file? |
| --- | --- | --- |
| `src/Envoy.Core/Translation/ITranslator.cs` | `TranslationException` gains `Kind`/`StatusCode`/`RetryAfter`; `BudgetExceededException` kind | |
| `src/Envoy.Core/Translation/TranslationErrors.cs` | new: `TranslationErrorKind`, `ProviderUnavailableException` | |
| `src/Envoy.Core/Translation/Providers/ProviderHttp.cs` | new: status classification, `Retry-After`, safe parse | |
| `src/Envoy.Core/Translation/Providers/GoogleFreeTranslator.cs` | use `ProviderHttp`; 403 → `RateLimited` | |
| `src/Envoy.Core/Translation/Providers/EdgeFreeTranslator.cs` | use `ProviderHttp`; 401 refresh-once | |
| `src/Envoy.Core/Translation/Providers/DeepLTranslator.cs` | use `ProviderHttp`; kinds | |
| `src/Envoy.Core/Translation/Providers/LibreTranslateTranslator.cs` | use `ProviderHttp`; extract `Parse` | |
| `src/Envoy.Core/Translation/Providers/ClaudeTranslator.cs` | injected `HttpClient`, `MaxRetries = 0`, timeout, exception mapping | |
| `src/Envoy.Core/Translation/Providers/AwsTranslator.cs` | `MaxErrorRetry = 0`, timeout, optional `HttpClientFactory`, error-code mapping | |
| `src/Envoy.Core/Translation/Resilience/ResilienceOptions.cs` | new | |
| `src/Envoy.Core/Translation/Resilience/RetryPolicy.cs` | new | |
| `src/Envoy.Core/Translation/Resilience/CircuitBreaker.cs` | new | |
| `src/Envoy.Core/Translation/Resilience/TokenBucket.cs` | new | |
| `src/Envoy.Core/Translation/Resilience/ProviderGuard.cs` | new | |
| `src/Envoy.Core/Translation/Resilience/ResilientTranslator.cs` | new | |
| `src/Envoy.Core/Translation/Resilience/TranslationDeadline.cs` | new (internal `AsyncLocal`) | |
| `src/Envoy.Core/Translation/Resilience/ProviderHealth.cs` | new: `ProviderState`, snapshot, registry, `ProviderHealthText` | |
| `src/Envoy.Core/Translation/CoalescingTranslator.cs` | new | |
| `src/Envoy.Core/Translation/TranslationCache.cs` | new | |
| `src/Envoy.Core/Translation/TranslationCacheStore.cs` | new | |
| `src/Envoy.Core/Translation/TranslationRuntime.cs` | new | |
| `src/Envoy.Core/Translation/CachingTranslator.cs` | uses `TranslationCache` + namespace; old ctor kept | |
| `src/Envoy.Core/Translation/FallbackTranslator.cs` | deadline, timeout fall-through, skip-unavailable, optional forced probe | |
| `src/Envoy.Core/Translation/TranslatorFactory.cs` | optional `TranslationRuntime`; new stack order; fingerprints | |
| `src/Envoy.Core/Translation/UsageTracker.cs` | `Refund`; `BudgetedTranslator` refunds and marks quota health | |
| `src/Envoy.Core/Settings/AppSettings.cs` | `TranslationSettings`: `RequestTimeoutSeconds = 15`, `RememberTranslations = false`, `CacheMaxEntries = 2000` | **yes** (`AppSettings.cs`) |
| `src/Envoy.App/AppHost.cs` | `TranslationRuntime` property; factory ctor arg; `Configure` in `ApplySettings`; health-driven `ProviderStatus`; `ProviderHealthChanged` event; `ActiveProviderIds`; flush on dispose; `HttpClient.Timeout` → 30 s | **yes** (`AppHost.cs`) |
| `src/Envoy.App/Pages/TranslationPage.xaml` (or the Translation section of `MainWindow.xaml` before 01 lands) | health card, remember checkbox + clear button, "Give up after" slider | **yes** (`MainWindow*` until 01 lands) |
| `src/Envoy.App/ViewModels/MainViewModel.Translation.cs` (or `MainViewModel.cs`) | `ProviderHealth` rows, `RetryProviderCommand`, `ClearTranslationCacheCommand`, cache count text, 1 s refresh while visible | |
| `src/Envoy.App/ViewModels/ProviderHealthRow.cs` | new row view model | |
| `src/Envoy.App/SmokeTest.cs` | seed health states; assert health rows render | **yes** (`SmokeTest.cs`) |
| `tests/Envoy.Core.Tests/Envoy.Core.Tests.csproj` | copy `Fixtures\**` to output | |
| `tests/Envoy.Core.Tests/Translation/*.cs`, `Fakes/*.cs`, `Fixtures/Providers/**` | new tests, helpers and fixtures (see test plan) | |
| `docs/translation-providers.md` | sections "When a provider fails" (retries, skipping, health card) and "Remembering translations" (privacy, location, clearing) | |
| `docs/architecture.md` | update the `ITranslator stack` line and the `Translation/` row | |
| `CHANGELOG.md` | entry | |

## Risks & open questions

1. **Size.** The full scope is about 1,400 lines of production code and tests, plus fixtures. That is at the top of
   the one-sitting budget. If it runs over, split it into two PRs: **(A)** error model, `ProviderHttp`, resilience,
   coalescing, health core, fallback fix and contract tests (REQ-TRR-1…17, 22–26); **(B)** cache persistence and
   UI (REQ-TRR-18…21, 28). (A) alone fixes the user-visible hang.
2. **Anthropic SDK details.** It is assumed that `AnthropicClient` accepts `HttpClient`, `MaxRetries` and `Timeout`
   (they are present in `Anthropic` 12.55.0's public API) and throws the typed exceptions listed above. Whether
   `Retry-After` is reachable from `AnthropicRateLimitException` is unverified. If not, Claude 429s use
   backoff/breaker only. The SDK may also validate response JSON strictly, so the success fixture must be a
   complete Messages API object.
3. **AWS contract tests** depend on `AmazonTranslateConfig.HttpClientFactory` working with a fake handler and on
   SigV4 signing not needing the network. This is a SHOULD. If it proves fragile, test only the
   `AmazonServiceException` → kind mapping with constructed exceptions.
4. **Fixture provenance.** This Linux sandbox cannot be relied on to reach the providers, so fixtures are written
   from the documented formats and the shapes in the current parse tests. Real captures (with keys and chat
   removed) should replace them when someone records them on Windows. `Fixtures/README.md` tracks this.
5. **Default limits for keyless endpoints are guesses.** The 2 req/s throttle and the 20 s breaker are not based on
   published limits. If users report throttling, tune `ResilienceOptions.For` in code. Should they become user
   settings? Proposed: no, until there is evidence.
6. **`AsyncLocal` deadline.** This keeps `ITranslator` unchanged, but the deadline is invisible to translators that
   start work on a fresh `ExecutionContext` (none do today). Alternative: pass the deadline in a
   `TranslationRequest` property, but that record must stay unchanged.
7. **Disk cache default off.** Off keeps the transcript-style privacy default. Paid users get the most out of it, so
   should onboarding (`onboarding-ux-accessibility`) suggest turning it on when a metered provider is chosen?
8. **Interaction with `chat-understanding`.** That area may change how `ChatPipeline` translates bursts (for
   example, cancelling pending translations on `NewMatch`). This spec relies only on the pipeline passing a
   `CancellationToken` and reacting to `TranslationException`, so the two should merge in either order.
9. **Interaction with `reliability-observability`.** That area should subscribe to `TranslationRuntime.Diagnostic`
   for logs rather than parsing exception messages. The diagnostic record is sanitised by construction.
10. **Merge order with `01-ui-foundation`.** The UI pieces should land after it, in `TranslationPage.xaml` and
    `MainViewModel.Translation.cs`, to avoid conflicts in `MainWindow.xaml`.
