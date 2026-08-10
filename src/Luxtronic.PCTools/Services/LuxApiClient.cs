using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

/// <summary>Thrown when POST .../test-runs is rejected by the server's concurrency rule (CONTRACT.md §6).</summary>
public sealed class ExclusiveConflictException : Exception
{
    public string? ActiveComponent { get; }

    public ExclusiveConflictException(string? activeComponent)
        : base($"Test run rejected by server concurrency rule; active component: {activeComponent ?? "(unspecified)"}")
    {
        ActiveComponent = activeComponent;
    }
}

/// <summary>
/// REST client for the PC-client-facing endpoints in CONTRACT.md §4. Every request carries
/// X-Api-Key per CONTRACT.md §1. This is the only place that talks HTTP to the server -
/// callers work with the typed request/response models in Models/Contracts.cs.
/// </summary>
public sealed class LuxApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Used when serializing outgoing request bodies. Omits null properties entirely (e.g. an
    /// unset CreateSessionRequest.SsdSerials) instead of writing them as JSON null - the server's
    /// POST /api/sessions validation only treats the field as "not provided" when it's absent,
    /// not when it's present-but-null (unlike its PATCH .../test-runs route, which explicitly
    /// tolerates both). Omitting nulls client-side sidesteps that inconsistency regardless of
    /// which route is doing the strict check.
    /// </summary>
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LuxApiClient(string serverBaseUrl, string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(serverBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ServerConfig> GetConfigAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/config", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var config = await resp.Content.ReadFromJsonAsync<ServerConfig>(JsonOptions, ct).ConfigureAwait(false);
        return config ?? throw new InvalidOperationException("GET /api/config returned an empty body.");
    }

    public async Task<string> CreateSessionAsync(CreateSessionRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/sessions", request, RequestJsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<CreateSessionResponse>(JsonOptions, ct).ConfigureAwait(false);
        return body?.SessionId ?? throw new InvalidOperationException("POST /api/sessions did not return session_id.");
    }

    /// <summary>
    /// Starts a test run. Throws <see cref="ExclusiveConflictException"/> on the 409 the
    /// concurrency rule (CONTRACT.md §6) can produce - only CPU is wired up this pass so this
    /// path is effectively dead for now, but implemented so it's ready when GPU/RAM/SSD land.
    /// </summary>
    public async Task<string> StartTestRunAsync(string sessionId, string component, CancellationToken ct = default)
    {
        var request = new StartTestRunRequest { Component = component };
        using var resp = await _http.PostAsJsonAsync($"/api/sessions/{sessionId}/test-runs", request, RequestJsonOptions, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await resp.Content.ReadFromJsonAsync<ExclusiveConflictResponse>(JsonOptions, ct).ConfigureAwait(false);
            throw new ExclusiveConflictException(conflict?.ActiveComponent);
        }

        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<StartTestRunResponse>(JsonOptions, ct).ConfigureAwait(false);
        return body?.TestRunId ?? throw new InvalidOperationException("POST .../test-runs did not return test_run_id.");
    }

    public async Task<string?> CompleteTestRunAsync(
        string sessionId, string testRunId, CompleteTestRunRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PatchAsJsonAsync(
            $"/api/sessions/{sessionId}/test-runs/{testRunId}", request, RequestJsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<CompleteTestRunResponse>(JsonOptions, ct).ConfigureAwait(false);
        return body?.Result;
    }

    public async Task EndSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var resp = await _http.PatchAsJsonAsync($"/api/sessions/{sessionId}", new EndSessionRequest(), RequestJsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException(
            $"{(int)resp.StatusCode} {resp.ReasonPhrase} from {resp.RequestMessage?.RequestUri}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
