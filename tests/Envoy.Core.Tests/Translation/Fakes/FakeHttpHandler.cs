using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Envoy.Core.Tests.Translation.Fakes;

/// <summary>One request as the handler saw it.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>
/// A fake transport: records every request (method, URI, headers, body) and answers from a queue of responders. When
/// the queue is empty the last responder repeats. Never touches the network.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _queue = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _last;
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate) return [.. _requests];
        }
    }

    public HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public FakeHttpHandler Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        lock (_gate) _queue.Enqueue(responder);
        return this;
    }

    public FakeHttpHandler Respond(ProviderFixture fixture) => Then((_, _) => Task.FromResult(fixture.ToResponse()));

    public FakeHttpHandler Respond(HttpStatusCode status, string body, string contentType = "application/json",
                                   IDictionary<string, string>? headers = null) =>
        Respond(new ProviderFixture((int)status, headers ?? new Dictionary<string, string>(), contentType, body));

    /// <summary>Never answers; ends only when the request is cancelled.</summary>
    public FakeHttpHandler Hang() => Then(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    });

    public FakeHttpHandler Throw(Exception ex) => Then((_, _) => Task.FromException<HttpResponseMessage>(ex));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        string? body = null;
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
            responder = _queue.Count > 0 ? (_last = _queue.Dequeue()) : _last ?? throw new InvalidOperationException("No response queued.");
        }
        var response = await responder(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    private static byte[] JsonToCbor(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var writer = new System.Formats.Cbor.CborWriter();
        Write(writer, doc.RootElement);
        return writer.Encode();

        static void Write(System.Formats.Cbor.CborWriter w, System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    w.WriteStartMap(null);
                    foreach (var p in e.EnumerateObject())
                    {
                        w.WriteTextString(p.Name);
                        Write(w, p.Value);
                    }
                    w.WriteEndMap();
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    w.WriteStartArray(null);
                    foreach (var item in e.EnumerateArray()) Write(w, item);
                    w.WriteEndArray();
                    break;
                case System.Text.Json.JsonValueKind.Number:
                    w.WriteDouble(e.GetDouble());
                    break;
                case System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False:
                    w.WriteBoolean(e.GetBoolean());
                    break;
                case System.Text.Json.JsonValueKind.Null:
                    w.WriteNull();
                    break;
                default:
                    w.WriteTextString(e.GetString() ?? "");
                    break;
            }
        }
    }
}

/// <summary>A recorded provider response: <c>{ "status", "headers", "contentType", "body" }</c>.</summary>
/// <remarks>
/// AWS Translate speaks Smithy RPC v2 CBOR: an <c>application/cbor</c> fixture's JSON body is converted to CBOR (unless
/// <c>"raw": true</c>, which sends the text as-is to simulate a malformed body).
/// </remarks>
internal sealed record ProviderFixture(int Status, IDictionary<string, string> Headers, string ContentType, string Body, bool Raw = false)
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Providers");

    /// <summary>Loads <c>Fixtures/Providers/{provider}/{scenario}.json</c>.</summary>
    public static ProviderFixture Load(string provider, string scenario)
    {
        var path = Path.Combine(Root, provider, scenario + ".json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var h))
            foreach (var p in h.EnumerateObject()) headers[p.Name] = p.Value.GetString() ?? "";
        return new ProviderFixture(
            root.GetProperty("status").GetInt32(),
            headers,
            root.TryGetProperty("contentType", out var ct) ? ct.GetString() ?? "application/json" : "application/json",
            root.GetProperty("body").GetString() ?? "",
            root.TryGetProperty("raw", out var raw) && raw.GetBoolean());
    }

    /// <summary>Loads a plain text file next to the fixtures (e.g. the Edge auth token).</summary>
    public static string Text(string provider, string file) => File.ReadAllText(Path.Combine(Root, provider, file)).Trim();

    public HttpResponseMessage ToResponse()
    {
        HttpContent content = ContentType.StartsWith("application/cbor", StringComparison.Ordinal) && !Raw
            ? new ByteArrayContent(JsonToCbor(Body))
            : new StringContent(Body, Encoding.UTF8);
        var response = new HttpResponseMessage((HttpStatusCode)Status) { Content = content };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
        foreach (var (name, value) in Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    private static byte[] JsonToCbor(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var writer = new System.Formats.Cbor.CborWriter();
        Write(writer, doc.RootElement);
        return writer.Encode();

        static void Write(System.Formats.Cbor.CborWriter w, System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    w.WriteStartMap(null);
                    foreach (var p in e.EnumerateObject())
                    {
                        w.WriteTextString(p.Name);
                        Write(w, p.Value);
                    }
                    w.WriteEndMap();
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    w.WriteStartArray(null);
                    foreach (var item in e.EnumerateArray()) Write(w, item);
                    w.WriteEndArray();
                    break;
                case System.Text.Json.JsonValueKind.Number:
                    w.WriteDouble(e.GetDouble());
                    break;
                case System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False:
                    w.WriteBoolean(e.GetBoolean());
                    break;
                case System.Text.Json.JsonValueKind.Null:
                    w.WriteNull();
                    break;
                default:
                    w.WriteTextString(e.GetString() ?? "");
                    break;
            }
        }
    }
}
