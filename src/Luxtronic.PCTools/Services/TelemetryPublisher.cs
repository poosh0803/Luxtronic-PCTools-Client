using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// PC client -> server leg of CONTRACT.md §5: connects to /ws/telemetry and pushes one JSON
/// message per sensor sample. Authenticates via the X-Api-Key request header on the upgrade
/// request - CONTRACT.md explicitly allows a ?api_key= query-param fallback for client
/// libraries that can't set headers on a WS handshake, but .NET's ClientWebSocket can set
/// headers directly, so the header form is used here (judgment call: only one auth path is
/// implemented client-side; server must still accept both per contract, but nothing on this
/// side exercises the query-param fallback).
/// </summary>
public sealed class TelemetryPublisher : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private static readonly JsonSerializerOptions JsonOptions = new();
    private bool _connected;

    public async Task ConnectAsync(string serverBaseUrl, string apiKey, CancellationToken ct = default)
    {
        var wsUri = ToWebSocketUri(serverBaseUrl, "/ws/telemetry");
        _socket.Options.SetRequestHeader("X-Api-Key", apiKey);
        await _socket.ConnectAsync(wsUri, ct).ConfigureAwait(false);
        _connected = true;
    }

    public async Task SendSampleAsync(TelemetrySample sample, CancellationToken ct = default)
    {
        if (!_connected || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Telemetry WebSocket is not connected.");
        }

        var json = JsonSerializer.Serialize(sample, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private static Uri ToWebSocketUri(string serverBaseUrl, string path)
    {
        var httpUri = new Uri(serverBaseUrl);
        var scheme = httpUri.Scheme == "https" ? "wss" : "ws";
        return new UriBuilder(httpUri) { Scheme = scheme, Path = path }.Uri;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connected && _socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close - the test run is already finishing either way.
            }
        }

        _socket.Dispose();
    }
}
