using System.Text;
using TopupLedger.Api.Http;
using TopupLedger.Application.Api;
using TopupLedger.Application.Security;
using TopupLedger.Domain.Partners;

namespace TopupLedger.Api.Security;

public sealed class PartnerRequestMiddleware
{
    public const string PartnerItem = "AuthenticatedPartner";
    public const string RawBodyItem = "RawBody";
    private readonly RequestDelegate _next;

    public PartnerRequestMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IPartnerAuthenticator authenticator, IPartnerRateLimiter rateLimiter, TimeProvider clock)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/partner"))
        {
            await _next(context);
            return;
        }

        context.Request.EnableBuffering();
        byte[] raw;
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            raw = Array.Empty<byte>();
        }
        else
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            raw = buffer.ToArray();
            context.Request.Body.Position = 0;
        }

        context.Items[RawBodyItem] = raw;
        var idempotency = context.Request.Headers["Idempotency-Key"].ToString();
        var auth = await authenticator.AuthenticateAsync(new PartnerAuthRequest(
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            context.Request.Headers["X-Api-Key"],
            context.Request.Headers["X-Timestamp"],
            context.Request.Headers["X-Signature"],
            string.IsNullOrEmpty(idempotency) ? null : idempotency,
            raw,
            IdempotencyKeyRequired: HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals("/api/v1/partner/topups", StringComparison.OrdinalIgnoreCase),
            clock.GetUtcNow()), context.RequestAborted);

        if (auth is PartnerAuthResult.Rejected rejected)
        {
            await ApiResultHttp.WriteAsync(context.Response, rejected.Error, context.RequestAborted);
            return;
        }

        var partner = ((PartnerAuthResult.Ok)auth).Partner;
        if (partner.Status == PartnerStatus.Suspended)
        {
            await ApiResultHttp.WriteAsync(
                context.Response,
                ApiResult.Problem(403, ErrorCodes.PartnerSuspended, "Partner is suspended."),
                context.RequestAborted);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Equals("/api/v1/partner/topups", StringComparison.OrdinalIgnoreCase))
        {
            var decision = await rateLimiter.AcquireAsync(partner.Id, clock.GetUtcNow().UtcDateTime, context.RequestAborted);
            if (!decision.Allowed)
            {
                await ApiResultHttp.WriteAsync(
                    context.Response,
                    ApiResult.Problem(429, ErrorCodes.RateLimited, "Rate limit exceeded.", "Retry after the indicated delay.", retryAfterSeconds: decision.RetryAfterSeconds),
                    context.RequestAborted);
                return;
            }
        }

        context.Items[PartnerItem] = partner;
        await _next(context);
    }
}
