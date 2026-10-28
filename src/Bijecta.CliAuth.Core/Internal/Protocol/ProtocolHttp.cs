using System.Text.Json;

namespace Bijecta.CliAuth.Core;

// One transport, no ambient cookies, redirect following, or retry of a one-use code.
internal static class ProtocolHttp
{
    private static readonly HttpMessageInvoker Transport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    internal static HttpClient Create(TimeProvider time, HttpMessageHandler? transport = null) => new(new BoundedHandler(time, transport is null ? Transport : new HttpMessageInvoker(transport, false)))
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private sealed class BoundedHandler(TimeProvider time, HttpMessageInvoker transport) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try
            {
                using var response = await transport.SendAsync(request, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
                var limit = request.Method == HttpMethod.Post ? 256 * 1024 : 1024 * 1024;
                if (response.Content.Headers.ContentLength > limit)
                {
                    throw new CliAuthException(AuthErrorCode.ProtocolFailure);
                }

                using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var bytes = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(bytes, linked.Token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + count > limit)
                    {
                        throw new CliAuthException(AuthErrorCode.ProtocolFailure);
                    }

                    buffer.Write(bytes, 0, count);
                }

                var result = new HttpResponseMessage(response.StatusCode)
                {
                    Content = new ByteArrayContent(buffer.ToArray())
                };
                // Only content type is needed by the protocol parser. Never forward cookies or redirects.
                result.Content.Headers.ContentType = response.Content.Headers.ContentType;
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CliAuthException(AuthErrorCode.NetworkFailure);
            }
            catch (HttpRequestException)
            {
                throw new CliAuthException(AuthErrorCode.NetworkFailure);
            }
            catch (IOException)
            {
                throw new CliAuthException(AuthErrorCode.NetworkFailure);
            }
        }
    }
}

internal static class ProtocolJson
{
    internal static JsonDocument Parse(string json)
    {
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            Check(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void Check(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new CliAuthException(AuthErrorCode.ProtocolFailure);
                }

                Check(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Check(item);
            }
        }
    }

    internal static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new CliAuthException(AuthErrorCode.ProtocolFailure);
}
