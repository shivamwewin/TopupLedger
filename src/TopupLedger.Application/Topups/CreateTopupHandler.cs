using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TopupLedger.Application.Api;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Security;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Money;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Privacy;
using TopupLedger.Domain.Time;

namespace TopupLedger.Application.Topups;

public sealed class CreateTopupHandler : IRequestHandler<CreateTopupCommand, ApiResult>
{
    private readonly ILedgerDbContext _db;
    private readonly IFinancialWriteStore _store;
    private readonly LedgerRules _rules;
    private readonly TopupTiming _timing;
    private readonly TimeProvider _clock;
    private readonly ILogger<CreateTopupHandler> _logger;

    public CreateTopupHandler(
        ILedgerDbContext db,
        IFinancialWriteStore store,
        LedgerRules rules,
        TopupTiming timing,
        TimeProvider clock,
        ILogger<CreateTopupHandler> logger)
    {
        _db = db;
        _store = store;
        _rules = rules;
        _timing = timing;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ApiResult> Handle(CreateTopupCommand command, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var hash = CanonicalSigner.HashBody(command.RawBody);
        if (!TryParse(command.RawBody, out var parsed, out var errors))
        {
            return ApiResult.Problem(400, ErrorCodes.ValidationFailed, "The request was invalid.", errors: errors);
        }

        var claim = await _store.ClaimIdempotencyAsync(
            command.PartnerId,
            command.IdempotencyKey,
            hash,
            now.UtcDateTime,
            _timing.IdempotencyLease,
            cancellationToken);

        switch (claim.Kind)
        {
            case IdempotencyClaimKind.Replay:
                _logger.LogInformation(
                    "Idempotent replay for partner {PartnerId} key {IdempotencyKey}",
                    command.PartnerId,
                    command.IdempotencyKey);
                return ApiResult.Stored(claim.HttpStatus!.Value, claim.Body!, claim.ContentType!, claim.Location);
            case IdempotencyClaimKind.InProgress:
                return InProgress();
            case IdempotencyClaimKind.KeyReused:
                return ApiResult.Problem(
                    422,
                    ErrorCodes.IdempotencyKeyReused,
                    "This idempotency key was already used with a different request body.");
        }

        var partner = await _db.Partners.AsNoTracking()
            .Where(p => p.Id == command.PartnerId)
            .Select(p => new { p.Id, p.ParentPartnerId })
            .SingleAsync(cancellationToken);

        var quote = CommissionCalculator.Quote(parsed.FaceFils, now, _rules.Rounding);
        var businessDay = BusinessDayCalculator.ForInstant(now, _rules.BusinessDayCutoff);
        var topupId = Guid.NewGuid();
        var provisional = new Topup
        {
            Id = topupId,
            PartnerId = partner.Id,
            ClientRef = parsed.ClientRef,
            Msisdn = parsed.Msisdn,
            Operator = parsed.Operator,
            FaceValueFils = quote.FaceValueFils,
            RetailerCommissionFils = quote.RetailerCommissionFils,
            DistributorShareFils = quote.DistributorShareFils,
            DebitFils = quote.PartnerDebitFils,
            Status = Domain.Topups.TopupStatus.Pending,
            BusinessDay = businessDay,
            IdempotencyKey = command.IdempotencyKey,
            CreatedAtUtc = now.UtcDateTime
        };
        var body = TopupBodies.From(provisional);
        var location = $"/api/v1/partner/topups/{topupId:D}";
        var response = ApiResult.Json(202, body, location);

        var written = await _store.CreatePendingTopupAsync(
            new PendingTopupDraft(
                topupId,
                partner.Id,
                command.IdempotencyKey,
                parsed.ClientRef,
                parsed.Msisdn,
                parsed.Operator,
                quote.FaceValueFils,
                quote.RetailerCommissionFils,
                quote.DistributorShareFils,
                quote.PartnerDebitFils,
                businessDay,
                now.UtcDateTime,
                command.CorrelationId,
                response.StatusCode,
                response.Body,
                response.ContentType,
                location),
            cancellationToken);

        if (written.Outcome == TopupWriteOutcome.StoredReplay)
        {
            return ApiResult.Stored(written.HttpStatus!.Value, written.Body!, written.ContentType!, written.Location);
        }

        if (written.Outcome == TopupWriteOutcome.InsufficientFunds)
        {
            var problem = ApiResult.Problem(
                402,
                ErrorCodes.InsufficientFunds,
                "Insufficient funds.",
                "Wallet balance is below the debit amount.");
            await _store.CompleteIdempotencyAsync(
                partner.Id,
                command.IdempotencyKey,
                problem.StatusCode,
                problem.Body,
                problem.ContentType,
                null,
                now.UtcDateTime,
                cancellationToken);
            _logger.LogInformation(
                "Top-up rejected for insufficient funds. Partner {PartnerId} msisdn {Msisdn} debitFils {DebitFils}",
                partner.Id,
                MsisdnMask.Apply(parsed.Msisdn),
                quote.PartnerDebitFils);
            return problem;
        }

        if (written.Outcome == TopupWriteOutcome.DuplicateClientRef)
        {
            var problem = ApiResult.Problem(
                409,
                ErrorCodes.DuplicateClientRef,
                "Duplicate client reference.",
                "clientRef has already been used by this partner.");
            await _store.CompleteIdempotencyAsync(
                partner.Id,
                command.IdempotencyKey,
                problem.StatusCode,
                problem.Body,
                problem.ContentType,
                null,
                now.UtcDateTime,
                cancellationToken);
            return problem;
        }

        _logger.LogInformation(
            "Top-up {TopupId} pending for partner {PartnerId} msisdn {Msisdn} debitFils {DebitFils} businessDay {BusinessDay}",
            written.Topup!.Id,
            partner.Id,
            MsisdnMask.Apply(parsed.Msisdn),
            quote.PartnerDebitFils,
            businessDay);

        return response;
    }

    private static ApiResult InProgress() =>
        ApiResult.Problem(
            409,
            ErrorCodes.RequestInProgress,
            "Request in progress.",
            "The same idempotency key is already being processed.");

    private static bool TryParse(byte[] raw, out ParsedTopup parsed, out Dictionary<string, string[]> errors)
    {
        parsed = default!;
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            errors["body"] = ["Request body must be JSON."];
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors["body"] = ["Request body must be a JSON object."];
                return false;
            }

            var root = doc.RootElement;
            var msisdnText = ReadString(root, "msisdn");
            var amountText = ReadAmount(root);
            var operatorText = ReadString(root, "operator");
            var clientRef = ReadString(root, "clientRef");

            if (!MsisdnNormalizer.TryNormalize(msisdnText, out var msisdn, out var msisdnError))
            {
                errors["msisdn"] = [msisdnError!];
            }

            if (!Fils.TryParseAed(amountText, out var face, out var amountError))
            {
                errors["amount"] = [amountError!];
            }

            MobileOperator claimed = default;
            if (!MobileOperatorParser.TryParse(operatorText, out claimed))
            {
                errors["operator"] = ["Operator must be ETISALAT or DU."];
            }
            else if (errors.Count == 0 && claimed != msisdn.Operator)
            {
                errors["operator"] = ["Operator does not match the MSISDN prefix."];
            }

            var client = clientRef?.Trim() ?? string.Empty;
            if (client.Length == 0 || client.Length > 64)
            {
                errors["clientRef"] = ["clientRef is required and must be at most 64 characters."];
            }

            if (errors.Count > 0)
            {
                return false;
            }

            parsed = new ParsedTopup(msisdn.Value, claimed, face, client);
            return true;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText().Trim('"');
    }

    private static string? ReadAmount(JsonElement root)
    {
        if (!root.TryGetProperty("amount", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private sealed record ParsedTopup(string Msisdn, MobileOperator Operator, long FaceFils, string ClientRef);
}
