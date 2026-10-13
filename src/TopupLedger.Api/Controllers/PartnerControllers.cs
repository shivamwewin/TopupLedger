using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using TopupLedger.Api.Http;
using TopupLedger.Api.Security;
using TopupLedger.Application.Security; 
using TopupLedger.Application.Topups;
using TopupLedger.Domain.Msisdn;

namespace TopupLedger.Api.Controllers;

[ApiController] 
[Route("api/v1/partner/topups")]
public sealed class TopupsController : ControllerBase
{
    private readonly IMediator _mediator;  

    public TopupsController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var partner = Partner();
        if (!Guid.TryParse(Request.Headers["Idempotency-Key"], out var key))
        {
            return BadRequest();
        }

        var raw = (byte[])(HttpContext.Items[PartnerRequestMiddleware.RawBodyItem] ?? Array.Empty<byte>());
        var result = await _mediator.Send(new CreateTopupCommand(partner.Id, raw, key, HttpContext.TraceIdentifier), cancellationToken);
        await ApiResultHttp.WriteAsync(Response, result, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOne(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetTopupQuery(Partner().Id, id), cancellationToken);
        await ApiResultHttp.WriteAsync(Response, result, cancellationToken);
        return new EmptyResult();
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var offset = Request.Query.ContainsKey("offset") || Request.Query.ContainsKey("page");
        var result = await _mediator.Send(new ListTopupsQuery(Partner().Id, status, from, to, limit, cursor, offset), cancellationToken);
        await ApiResultHttp.WriteAsync(Response, result, cancellationToken);
        return new EmptyResult();
    }

    private AuthenticatedPartner Partner() =>
        (AuthenticatedPartner)HttpContext.Items[PartnerRequestMiddleware.PartnerItem]!;
}

[ApiController]
[Route("api/v1/partner/wallet")]
public sealed class WalletController : ControllerBase
{
    private readonly IMediator _mediator;

    public WalletController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var partner = (AuthenticatedPartner)HttpContext.Items[PartnerRequestMiddleware.PartnerItem]!;
        var result = await _mediator.Send(new GetWalletQuery(partner.Id), cancellationToken);
        await ApiResultHttp.WriteAsync(Response, result, cancellationToken);
        return new EmptyResult();
    }
}

[ApiController]
[Route("api/v1/partner/settlements")]
public sealed class SettlementsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SettlementsController(IMediator mediator) => _mediator = mediator;

    [HttpGet("{businessDay}")]
    public async Task<IActionResult> Get(string businessDay, CancellationToken cancellationToken)
    {
        var partner = (AuthenticatedPartner)HttpContext.Items[PartnerRequestMiddleware.PartnerItem]!;
        var result = await _mediator.Send(new GetSettlementQuery(partner.Id, businessDay), cancellationToken);
        await ApiResultHttp.WriteAsync(Response, result, cancellationToken);
        return new EmptyResult();
    }
}

[ApiController]
[Route("api/v1/operator/callbacks")]
public sealed class OperatorCallbacksController : ControllerBase
{
    private readonly IMediator _mediator;

    public OperatorCallbacksController(IMediator mediator) => _mediator = mediator;

    [HttpPost("{operator}")]
    public async Task<IActionResult> Post(string @operator, CancellationToken cancellationToken)
    {
        if (!MobileOperatorParser.TryParseRoute(@operator, out var parsed))
        {
            await ApiResultHttp.WriteAsync(
                Response,
                Application.Api.ApiResult.Problem(404, Application.Api.ErrorCodes.NotFound, "Operator was not found."),
                cancellationToken);
            return new EmptyResult();
        }

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            await ApiResultHttp.WriteAsync(
                Response,
                Application.Api.ApiResult.Problem(400, Application.Api.ErrorCodes.ValidationFailed, "The request was invalid.", errors: new Dictionary<string, string[]> { ["body"] = ["Callback body must be JSON."] }),
                cancellationToken);
            return new EmptyResult();
        }

        using (doc)
        {
            var root = doc.RootElement;
            var txn = root.TryGetProperty("operatorTxnId", out var txnEl) ? txnEl.GetString() : null;
            var topupText = root.TryGetProperty("topupId", out var idEl) ? idEl.GetString() : null;
            Guid.TryParse(topupText, out var topupId);
            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            var seq = root.TryGetProperty("eventSeq", out var seqEl) && seqEl.TryGetInt32(out var n) ? n : 0;
            var result = await _mediator.Send(
                new ApplyOperatorCallbackCommand(parsed, Request.Headers["X-Operator-Token"], txn, topupId, status, seq),
                cancellationToken);
            await ApiResultHttp.WriteAsync(Response, result, cancellationToken);
            return new EmptyResult();
        }
    }
}
