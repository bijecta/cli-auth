using System.Text.Json;
using System.Text.Json.Serialization;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.Sample.Cli;

// One credential-free output line; absent values are omitted. Serialized by the source generator,
// not reflection, so the sample stays Native AOT compatible.
internal sealed record SampleOutput(
    string? Action = null,
    string? Profile = null,
    PersistenceMode? Persistence = null,
    string? Subject = null,
    DateTimeOffset? ExpiresAtUtc = null,
    IReadOnlyList<string>? GrantedScopes = null,
    SessionStatusKind? Kind = null,
    StatusOutput? Status = null,
    bool? LocalSessionRemoved = null,
    RevocationOutcome? RevocationOutcome = null,
    AuthErrorCode? Error = null)
{
    internal static void Print(SampleOutput output) =>
        Console.WriteLine(JsonSerializer.Serialize(output, SampleJsonContext.Default.SampleOutput));
}

internal sealed record StatusOutput(SessionStatusKind Kind, DateTimeOffset? ExpiresAtUtc, string? Subject, string? Issuer)
{
    internal static StatusOutput From(SessionStatus status) =>
        new(status.Kind, status.ExpiresAtUtc, status.Subject, status.Issuer?.OriginalString);
}

internal sealed record ErrorOutput(AuthErrorCode ErrorCode, string? ProviderErrorCode, string Message, Guid CorrelationId)
{
    internal static void Print(CliAuthException error) => Console.Error.WriteLine(JsonSerializer.Serialize(
        new ErrorOutput(error.ErrorCode, error.ProviderErrorCode, error.Message, error.CorrelationId),
        SampleJsonContext.Default.ErrorOutput));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SampleOutput))]
[JsonSerializable(typeof(ErrorOutput))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
