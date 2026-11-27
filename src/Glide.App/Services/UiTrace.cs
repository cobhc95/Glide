using Glide.Diagnostics.Runtime;

namespace Glide.App.Services;

/// <summary>
/// Opt-in, step-by-step traces for two behaviours that headless tests passed but the real desktop did not: the "Alt+drag to
/// move" hint on the text overlay and an Explorer thumbnail's middle-click. Lines go to <c>startup-trace.log</c> (next to
/// glide.settings.json, <see cref="StartupTrace"/>), prefixed with the channel.
/// <list type="bullet">
/// <item><c>GLIDE_TRACE_HINT=1</c>: the hint channel (hover result, timer start / tick, guards, show result and reason, geometry).</item>
/// <item><c>GLIDE_TRACE_INPUT=1</c>: the Explorer pointer channel (press, entry under the pointer, open decision, tab added or not).</item>
/// <item>The live diagnostic trace (<c>--diagnostic-trace</c> / <c>GLIDE_DIAGNOSTIC_TRACE=1</c>) switches both on.</item>
/// </list>
/// Off, a call costs one boolean read; no payload is built (callers test <see cref="Hint"/> / <see cref="Input"/> first).
/// </summary>
internal static class UiTrace
{
    private static bool? _hintForTests;
    private static bool? _inputForTests;
    private static readonly bool s_hintEnv = EnvOn("GLIDE_TRACE_HINT");
    private static readonly bool s_inputEnv = EnvOn("GLIDE_TRACE_INPUT");

    /// <summary>Tests: receives every line instead of the log file (null: the log file).</summary>
    internal static Action<string>? SinkForTests { get; set; }

    internal static bool Hint => _hintForTests ?? (s_hintEnv || LiveDiagnosticTrace.IsEnabled);
    internal static bool Input => _inputForTests ?? (s_inputEnv || LiveDiagnosticTrace.IsEnabled);

    /// <summary>Tests: force a channel on or off (null: follow the environment).</summary>
    internal static void SetForTests(bool? hint, bool? input) { _hintForTests = hint; _inputForTests = input; }

    internal static void WriteHint(string step, string? detail = null) { if (Hint) Write("hint", step, detail); }

    internal static void WriteInput(string step, string? detail = null) { if (Input) Write("input", step, detail); }

    private static void Write(string channel, string step, string? detail)
    {
        try
        {
            if (SinkForTests is { } sink) { sink(detail is null ? $"{channel}.{step}" : $"{channel}.{step} {detail}"); return; }
            StartupTrace.Write($"trace_{channel}_{step}", detail);
        }
        catch { /* tracing must never disturb input */ }
    }

    private static bool EnvOn(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
