namespace Luxtronic.PCTools.Services;

public enum TestKind { Cpu, Gpu, Vram, Ram, Ssd }

/// <summary>What one finished run produced, kept on the PC only. The Run Tests series submits the
/// same numbers to the server; a single-test tab keeps them local (see SingleTestViewModel).</summary>
public sealed record LocalRunSummary(string Component, IReadOnlyDictionary<string, object> Stats, string? StopReason);

/// <summary>An already-open server session that several test runs share (the Run Tests series).
/// Passed to the per-test methods so they add a test run to it instead of opening/ending their own.</summary>
public sealed class SeriesSession
{
    public SeriesSession(string sessionId) => SessionId = sessionId;

    public string SessionId { get; }

    public List<LocalRunSummary> Results { get; } = new();
}
