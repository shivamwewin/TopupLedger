using TopupLedger.Application.Api;

namespace TopupLedger.Api.Http;

public static class ApiResultHttp
{
    public static async Task WriteAsync(HttpResponse response, ApiResult result, CancellationToken cancellationToken)
    {
        response.StatusCode = result.StatusCode;
        response.ContentType = result.ContentType;
        if (!string.IsNullOrEmpty(result.Location))
        {
            response.Headers.Location = result.Location;
        }

        if (result.RetryAfterSeconds is > 0)
        {
            response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
        }

        await response.Body.WriteAsync(result.Body, cancellationToken);
    }
}
