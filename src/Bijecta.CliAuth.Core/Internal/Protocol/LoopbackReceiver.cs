using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Bijecta.CliAuth.Core;

[DebuggerDisplay("BrowserResponse (redacted)")]
internal sealed class BrowserResponse(string? code, string? error)
{
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string? Code { get; } = code;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string? Error { get; } = error;
    public override string ToString() => "BrowserResponse (redacted)";
}
// A single transaction, bound before launch, accepting only origin-form HTTP on IPv4 loopback.
internal sealed class LoopbackReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(4);
    private readonly List<Task> _connections = [];
    private readonly TaskCompletionSource<BrowserResponse> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly string _issuer;
    private Task? _accept;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string _state = "";
    private int _rejections;
    internal LoopbackReceiver(BrowserCallbackOptions options, Uri issuer, TimeProvider time)
    {
        _time = time;
        _path = options.Path;
        _issuer = issuer.OriginalString;
        _listener = new TcpListener(IPAddress.Loopback, options.Port);
        try
        {
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(8);
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            RedirectUri = new Uri($"http://127.0.0.1:{port}{_path}");
        }
        catch
        {
            _listener.Stop();
            throw new CliAuthException(AuthErrorCode.CallbackRejected);
        }
    }

    internal Uri RedirectUri { get; }
    internal Task<BrowserResponse> Response => _result.Task;

    internal void Start(string state)
    {
        _state = state;
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }

                var task = HandleAsync(client);
                // Only four unfinished tasks are retained, even under hostile local traffic.
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
        catch
        {
            _result.TrySetException(new CliAuthException(AuthErrorCode.CallbackRejected));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, deadline.Token))
        {
            var accepted = false;
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var length = 0;
                var end = -1;
                while (length < buffer.Length && end < 0)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(length), linked.Token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    length += count;
                    for (var i = Math.Max(0, length - count - 3); i <= length - 4; i++)
                    {
                        if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                        {
                            end = i + 4;
                            break;
                        }
                    }
                }

                var response = end == length && end >= 0 ? Parse(buffer.AsSpan(0, end)) : null;
                if (response is not null)
                {
                    accepted = _result.TrySetResult(response);
                }

                var body = accepted ? "Login response received. You may close this window." : "Callback rejected.";
                var reply = $"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                _slots.Release();
                if (accepted)
                {
                    await _stop.CancelAsync().ConfigureAwait(false);
                    _listener.Stop();
                }
                if (!accepted && Interlocked.Increment(ref _rejections) >= 20)
                {
                    _result.TrySetException(new CliAuthException(AuthErrorCode.CallbackRejected));
                }
            }
        }
    }

    private BrowserResponse? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            foreach (var b in bytes)
            {
                if (b > 126 || b < 32 && b != 13 && b != 10 && b != 9)
                {
                    return null;
                }
            }

            var lines = Encoding.ASCII.GetString(bytes).Split("\r\n", StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3 || first[0] != "GET" || first[2] != "HTTP/1.1" || first[1].Length > 8192)
            {
                return null;
            }

            var target = first[1];
            if (target.Contains('#'))
            {
                return null;
            }

            var question = target.IndexOf('?');
            if (question < 0 || target[..question] != _path)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).TakeWhile(x => x.Length != 0))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                {
                    return null;
                }
            }

            if (!headers.TryGetValue("Host", out var host) || host != RedirectUri.Authority || headers.ContainsKey("Transfer-Encoding") || headers.TryGetValue("Content-Length", out var size) && size != "0")
            {
                return null;
            }

            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in target[(question + 1)..].Split('&'))
            {
                var pair = item.Split('=', 2);
                if (pair.Length != 2 || !query.TryAdd(Decode(pair[0]), Decode(pair[1])))
                {
                    return null;
                }
            }

            if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(_state)))
            {
                return null;
            }

            if (query.TryGetValue("iss", out var issuer) && issuer != _issuer)
            {
                return null;
            }

            query.TryGetValue("code", out var code);
            query.TryGetValue("error", out var error);
            if ((code is null) == (error is null) || code is not null && string.IsNullOrWhiteSpace(code) || error is not null && string.IsNullOrWhiteSpace(error))
            {
                return null;
            }

            return new(code, error);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Decode(string value)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                {
                    throw new FormatException();
                }

                bytes.WriteByte(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.WriteByte(c == '+' ? (byte)' ' : (byte)c);
            }
        }

        var decoded = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        if (decoded.Any(char.IsControl))
        {
            throw new FormatException();
        }

        return decoded;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_accept is not null)
        {
            await _accept.ConfigureAwait(false);
        }

        await Task.WhenAll(_connections).ConfigureAwait(false);
        _result.TrySetCanceled();
        if (_result.Task.IsFaulted)
        {
            _ = _result.Task.Exception;
        }
        _state = string.Empty;
        _stop.Dispose();
        _slots.Dispose();
    }
}
