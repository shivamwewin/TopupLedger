using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TopupLedger.Api.OpenApi;

public sealed class HmacOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? string.Empty;
        if (!path.StartsWith("api/v1/partner", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("operator/callbacks", StringComparison.OrdinalIgnoreCase))
            {
                operation.Parameters ??= new List<OpenApiParameter>();
                operation.Parameters.Add(Header("X-Operator-Token", "Per-operator shared secret. Compared in constant time."));
            }

            return;
        }

        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(Header("X-Api-Key", "Partner API key."));
        operation.Parameters.Add(Header("X-Timestamp", "Unix seconds, UTC. Rejected when more than 300 seconds from the server clock."));
        operation.Parameters.Add(Header("X-Signature", "Lowercase hex HMAC-SHA256 of the canonical string. The body hash is SHA-256 of the raw bytes."));
        if (HttpMethods.IsPost(context.ApiDescription.HttpMethod ?? string.Empty))
        {
            operation.Parameters.Add(Header("Idempotency-Key", "UUID. Required on POST /topups. Replays return the original response body."));
        }

        operation.Description = (operation.Description ?? string.Empty) +
            " Canonical string fields, joined by \\n with no trailing newline: METHOD, PATH (no query or host), X-Timestamp, Idempotency-Key (empty on GET), lowercase hex SHA-256 of the raw body (empty body on GET).";
    }

    private static OpenApiParameter Header(string name, string description) => new()
    {
        Name = name,
        In = ParameterLocation.Header,
        Required = true,
        Description = description,
        Schema = new OpenApiSchema { Type = "string" }
    };
}
