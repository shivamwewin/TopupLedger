using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopupLedger.Application.Api;

public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string InvalidSignature = "AUTH_INVALID_SIGNATURE";
    public const string ClockSkew = "AUTH_CLOCK_SKEW";
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string NotFound = "NOT_FOUND";
    public const string RequestInProgress = "REQUEST_IN_PROGRESS";
    public const string DuplicateClientRef = "DUPLICATE_CLIENT_REF";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string RateLimited = "RATE_LIMITED";
    public const string PartnerSuspended = "PARTNER_SUSPENDED";
}

public sealed class ApiResult
{
    public int StatusCode { get; }
    public byte[] Body { get; }
    public string ContentType { get; }
    public string? Location { get; }
    public int? RetryAfterSeconds { get; }

    private ApiResult(int statusCode, byte[] body, string contentType, string? location, int? retryAfterSeconds)
    {
        StatusCode = statusCode;
        Body = body;
        ContentType = contentType;
        Location = location;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public static ApiResult Json(int statusCode, object payload, string? location = null) =>
        new(statusCode, JsonSerializer.SerializeToUtf8Bytes(payload, JsonDefaults.Options), "application/json", location, null);

    public static ApiResult Problem(
        int statusCode,
        string code,
        string title,
        string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        int? retryAfterSeconds = null)
    {
        var payload = new ProblemPayload(
            $"https://topupledger.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            title,
            statusCode,
            code,
            detail,
            errors);
        return new ApiResult(
            statusCode,
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonDefaults.Options),
            "application/problem+json",
            null,
            retryAfterSeconds);
    }

    public static ApiResult Stored(int statusCode, byte[] body, string contentType, string? location) =>
        new(statusCode, body, contentType, location, null);
}

public sealed record ProblemPayload(
    string Type,
    string Title,
    int Status,
    string Code,
    string? Detail,
    IReadOnlyDictionary<string, string[]>? Errors);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
