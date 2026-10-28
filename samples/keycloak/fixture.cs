#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = Path.GetDirectoryName(SourcePath())!;

if (args is ["prepare"])
{
    var local = Path.Combine(root, ".local");
    if (Directory.Exists(local))
    {
        Console.Error.WriteLine(".local already exists. Reuse it, or stop/remove the fixture before explicitly deleting it.");
        return 1;
    }
    if (OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(local);
    }
    else
    {
        Directory.CreateDirectory(local, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    var adminPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    var userPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    var realm = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "realm.template.json")))!.AsObject();
    realm["users"] = new JsonArray(
        new JsonObject
        {
            ["username"] = "learner",
            ["enabled"] = true,
            ["emailVerified"] = true,
            ["firstName"] = "Development",
            ["lastName"] = "User",
            ["email"] = "learner@example.test",
            ["requiredActions"] = new JsonArray(),
            ["credentials"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "password",
                    ["value"] = userPassword,
                    ["temporary"] = false
                })
        }
    );
    WritePrivate(Path.Combine(local, "realm.json"), realm.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    if (!OperatingSystem.IsWindows())
    {
        // The container's keycloak user must read the bind-mounted import file.
        // The host's enclosing .local directory remains owner-only (0700).
        File.SetUnixFileMode(Path.Combine(local, "realm.json"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
    WritePrivate(Path.Combine(local, "keycloak.env"),
        $"KC_BOOTSTRAP_ADMIN_USERNAME=local-admin\nKC_BOOTSTRAP_ADMIN_PASSWORD={adminPassword}\n");
    WritePrivate(Path.Combine(local, "credentials.txt"),
        $"Disposable local Keycloak only\nLogin user: learner\nLogin password: {userPassword}\nAdmin user: local-admin\nAdmin password: {adminPassword}\n");
    Console.WriteLine("Generated disposable credentials in samples/keycloak/.local/credentials.txt.");
    return 0;
}

if (args is ["wait"])
{
    const string issuer = "http://127.0.0.1:8080/realms/cliauth";
    using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 1024 * 1024 };
    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try
    {
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(issuer + "/.well-known/openid-configuration", budget.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
                    if (metadata.RootElement.GetProperty("issuer").GetString() != issuer)
                    {
                        Console.Error.WriteLine("Unexpected issuer. Check the fixture hostname configuration.");
                        return 1;
                    }
                    Console.WriteLine("Keycloak development realm is ready.");
                    return 0;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!budget.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(1), budget.Token);
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Keycloak did not become ready within two minutes. Inspect docker compose logs.");
        return 1;
    }
}
Console.Error.WriteLine("Usage: dotnet run --file samples/keycloak/fixture.cs -- prepare|wait");
return 2;

static string SourcePath([CallerFilePath] string path = "") => path;

static void WritePrivate(string path, string content)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (!OperatingSystem.IsWindows())
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }
    using var stream = new FileStream(path, options);
    using var writer = new StreamWriter(stream);
    writer.Write(content);
}
