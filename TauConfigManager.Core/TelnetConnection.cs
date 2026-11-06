using System.Net.Sockets;
using System.Text;

namespace TauConfigManager.Core;

public sealed class CommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
}

public sealed class TelnetConnection : IAsyncDisposable
{
    private static readonly TimeSpan InterLineDelay = TimeSpan.FromMilliseconds(15);
    private readonly TauConnectionOptions _options;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TelnetConnection(TauConnectionOptions options)
    {
        _options = options;
    }

    public async Task ConnectAndLoginAsync(CancellationToken cancellationToken = default)
    {
        _client = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_options.Timeout);
        try
        {
            await _client.ConnectAsync(_options.Host, _options.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"连接设备 {_options.Host}:{_options.Port} 超时（{_options.Timeout.TotalSeconds:N0} 秒）。");
        }

        _stream = _client.GetStream();

        await ReadUntilAsync(["login:", "Login:"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        await SendLineAsync(_options.UserName, cancellationToken).ConfigureAwait(false);
        await ReadUntilAsync(["Password:"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        await SendLineAsync(_options.Password, cancellationToken).ConfigureAwait(false);
        await ReadUntilAsync(["# ", "$ "], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandResult> RunCommandAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return await RunCommandLinesAsync([command], timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> RunCommandLinesAsync(IReadOnlyList<string> commandLines, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var token = "__TAU_TOOL_DONE_" + Guid.NewGuid().ToString("N") + "__";
        foreach (var line in commandLines)
        {
            await SendLineWithPacingAsync(line, cancellationToken).ConfigureAwait(false);
        }

        await SendLineWithPacingAsync("rc=$?", cancellationToken).ConfigureAwait(false);
        foreach (var line in BuildSplitTokenEchoLines(token, "tau_done", ":$rc"))
        {
            await SendLineWithPacingAsync(line, cancellationToken).ConfigureAwait(false);
        }

        var output = await ReadUntilAsync([token + ":"], timeout ?? _options.Timeout, cancellationToken).ConfigureAwait(false);
        return ParseCommandResult(output, token);
    }

    public async Task<CommandResult> RunScriptAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var scriptName = "/tmp/tau_tool_" + Guid.NewGuid().ToString("N") + ".sh";
        var uploadToken = "__TAU_UPLOAD_DONE_" + Guid.NewGuid().ToString("N") + "__";
        await TrySetTerminalEchoAsync(false, cancellationToken).ConfigureAwait(false);
        try
        {
            await SendLineWithPacingAsync($"cat > {scriptName} <<'__TAU_TOOL_EOF__'", cancellationToken).ConfigureAwait(false);
            foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
            {
                await SendLineWithPacingAsync(line, cancellationToken).ConfigureAwait(false);
            }

            await SendLineWithPacingAsync("__TAU_TOOL_EOF__", cancellationToken).ConfigureAwait(false);
            foreach (var line in BuildSplitTokenEchoLines(uploadToken, "tau_upload"))
            {
                await SendLineWithPacingAsync(line, cancellationToken).ConfigureAwait(false);
            }

            await ReadUntilAsync([uploadToken], timeout ?? _options.Timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await TrySetTerminalEchoAsync(true, CancellationToken.None).ConfigureAwait(false);
        }

        return await RunCommandLinesAsync(
            [
                $"sh {scriptName}",
                "script_rc=$?",
                $"rm -f {scriptName}",
                "if [ $script_rc -eq 0 ]; then true; else false; fi",
            ],
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream != null)
        {
            try
            {
                await SendLineAsync("exit", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore disconnect errors during cleanup.
            }

            _stream.Dispose();
        }

        _client?.Dispose();
    }

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Telnet 尚未连接。");
        }

        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLineWithPacingAsync(string line, CancellationToken cancellationToken)
    {
        await SendLineAsync(line, cancellationToken).ConfigureAwait(false);
        await Task.Delay(InterLineDelay, cancellationToken).ConfigureAwait(false);
    }

    private async Task TrySetTerminalEchoAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            await SendLineAsync(enabled ? "stty echo 2>/dev/null || true" : "stty -echo 2>/dev/null || true", cancellationToken).ConfigureAwait(false);
            await ReadUntilAsync(["# ", "$ "], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Some firmware builds do not expose stty. Script execution still works
            // with pacing; echo control only reduces Telnet back-pressure.
        }
    }

    private static string BuildWrappedCommand(string command, string token)
    {
        return $"{command}; rc=$?; {BuildSplitTokenEchoCommand(token, "tau_done", ":$rc")}";
    }

    private static string BuildSplitTokenEchoCommand(string token, string variablePrefix, string suffix = "")
    {
        return string.Join("; ", BuildSplitTokenEchoLines(token, variablePrefix, suffix));
    }

    private static IReadOnlyList<string> BuildSplitTokenEchoLines(string token, string variablePrefix, string suffix = "")
    {
        var split = token.Length / 2;
        var variableA = variablePrefix + "_a";
        var variableB = variablePrefix + "_b";
        var tokenA = ShellSingleQuote(token[..split]);
        var tokenB = ShellSingleQuote(token[split..]);
        return
        [
            $"{variableA}={tokenA}",
            $"{variableB}={tokenB}",
            "echo \"$" + variableA + "$" + variableB + suffix + "\"",
        ];
    }

    private static string ShellSingleQuote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private async Task<string> ReadUntilAsync(IReadOnlyList<string> tokens, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Telnet 尚未连接。");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[8192];
        var text = new StringBuilder();

        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                text.Append(TelnetProtocol.DecodeText(buffer.AsSpan(0, read).ToArray()));
                var current = text.ToString();
                if (tokens.Any(token => current.Contains(token, StringComparison.Ordinal)))
                {
                    return current;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var partial = text.ToString();
            if (partial.Length > 4000)
            {
                partial = partial[^4000..];
            }

            throw new TimeoutException($"等待设备输出超时（{timeout.TotalSeconds:N0} 秒）。最后收到的输出：{partial}");
        }

        return text.ToString();
    }

    private static CommandResult ParseCommandResult(string output, string token)
    {
        var markerIndex = output.LastIndexOf(token + ":", StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return new CommandResult
            {
                ExitCode = -1,
                Output = output,
            };
        }

        var after = output[(markerIndex + token.Length + 1)..];
        var codeText = new string(after.TakeWhile(char.IsDigit).ToArray());
        _ = int.TryParse(codeText, out var exitCode);

        return new CommandResult
        {
            ExitCode = exitCode,
            Output = output[..markerIndex],
        };
    }
}
