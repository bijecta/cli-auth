using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

internal static class CoreTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    internal static OidcProfile Profile(string application = "sample.cli", string name = "work",
        string issuer = "https://issuer.example.test", string client = "sample-client",
        IEnumerable<string>? scopes = null, BrowserCallbackOptions? callback = null,
        bool allowLoopbackHttp = false, TimeSpan? margin = null,
        PersistenceMode persistence = PersistenceMode.MemoryOnly) =>
        new(application, name, new Uri(issuer), client, scopes, persistence, callback, allowLoopbackHttp, margin);

    internal static StoredSession Session(DateTimeOffset? expiry = null, string? refresh = "synthetic-refresh",
        SessionLifecycleState state = SessionLifecycleState.Active, long generation = 0) =>
        new(generation, Guid.NewGuid(), state,
            new AccessToken("synthetic-access", expiry ?? Now.AddMinutes(5), ["api.read"]), refresh,
            new Uri("https://issuer.example.test"), "sample-client", "synthetic-subject");

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
