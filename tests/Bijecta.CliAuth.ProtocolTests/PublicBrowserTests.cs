using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bijecta.CliAuth.Core;
using Microsoft.IdentityModel.Tokens;

namespace Bijecta.CliAuth.ProtocolTests;

public sealed class PublicBrowserTests
{
    [Fact]
    public async Task PublicConstructorUsesRealDiscoveryAndTokenTransport()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var issuer = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/realm";
        using var rsa = RSA.Create(2048);
        string? nonce = null;
        Dictionary<string, string>? front = null;
        var requests = new List<string>();
        var server = ServeAsync();
        var profile = new OidcProfile("public-test", "work", new Uri(issuer), "native-cli",
            persistence: PersistenceMode.MemoryOnly, allowLoopbackHttp: true);
        var auth = new CliAuthenticator([profile], new MemoryTokenStore(), browser: new Browser(async (uri, token) =>
        {
            front = BrowserFixture.Query(uri.Query);
            nonce = front["nonce"];
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var reply = await http.GetAsync(front["redirect_uri"] + "?state=" + front["state"] + "&code=synthetic-public-code", token);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        }));
        try
        {
            var login = await auth.LoginAsync("work", new LoginOptions(), ct);
            Assert.Equal("public-subject", login.Subject);
            Assert.Equal("public-access", (await auth.GetAccessTokenAsync("work", ct)).Value);
            await server;
            Assert.Equal(["GET /realm/.well-known/openid-configuration HTTP/1.1", "GET /realm/keys HTTP/1.1", "POST /realm/token HTTP/1.1"], requests);
        }
        finally
        {
            await deadline.CancelAsync();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
        }

        async Task ServeAsync()
        {
            for (var i = 0; i < 3; i++)
            {
                using var socket = await listener.AcceptTcpClientAsync(ct);
                var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var first = await reader.ReadLineAsync(ct);
                requests.Add(first!);
                var length = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(line[15..]);
                    }
                }
                object response;
                if (i == 0)
                {
                    response = new
                    {
                        issuer,
                        authorization_endpoint = issuer + "/authorize",
                        token_endpoint = issuer + "/token",
                        jwks_uri = issuer + "/keys",
                        response_types_supported = new[] { "code" },
                        code_challenge_methods_supported = new[] { "S256" }
                    };
                }
                else if (i == 1) { response = BrowserFixture.Jwks(rsa, "public-key"); }
                else
                {
                    var buffer = new char[length];
                    Assert.Equal(length, await reader.ReadBlockAsync(buffer.AsMemory(), ct));
                    var form = BrowserFixture.Query(new string(buffer));
                    Assert.Equal(front!["redirect_uri"], form["redirect_uri"]);
                    Assert.Equal("synthetic-public-code", form["code"]);
                    var now = DateTime.UtcNow;
                    var id = new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
                    {
                        Issuer = issuer,
                        Audience = "native-cli",
                        Subject = new ClaimsIdentity([new Claim("sub", "public-subject"), new Claim("nonce", nonce!)]),
                        IssuedAt = now,
                        NotBefore = now,
                        Expires = now.AddMinutes(5),
                        SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "public-key" }, SecurityAlgorithms.RsaSha256)
                    });
                    response = new { access_token = "public-access", token_type = "Bearer", expires_in = 300, id_token = id };
                }
                var body = JsonSerializer.Serialize(response);
                var reply = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), ct);
            }
        }
    }

    private sealed class Browser(Func<Uri, CancellationToken, Task> open) : ISystemBrowser
    {
        public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default) => open(authorizationUri, cancellationToken);
    }
}
