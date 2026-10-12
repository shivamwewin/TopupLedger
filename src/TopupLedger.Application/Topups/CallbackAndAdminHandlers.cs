using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TopupLedger.Application.Api;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Security;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Partners;
using TopupLedger.Domain.Privacy;

namespace TopupLedger.Application.Topups;

public sealed record OperatorTokens(string Etisalat, string Du);

public sealed class ApplyOperatorCallbackHandler : IRequestHandler<ApplyOperatorCallbackCommand, ApiResult>
{
    private readonly IFinancialWriteStore _store;
    private readonly OperatorTokens _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<ApplyOperatorCallbackHandler> _logger;

    public ApplyOperatorCallbackHandler(
        IFinancialWriteStore store,
        OperatorTokens tokens,
        TimeProvider clock,
        ILogger<ApplyOperatorCallbackHandler> logger)
    {
        _store = store;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ApiResult> Handle(ApplyOperatorCallbackCommand command, CancellationToken cancellationToken)
    {
        var expected = command.Operator == MobileOperator.Etisalat ? _tokens.Etisalat : _tokens.Du;
        if (string.IsNullOrEmpty(expected) || !FixedSecretEquals(expected, command.OperatorToken))
        {
            return ApiResult.Problem(401, ErrorCodes.InvalidSignature, "Authentication failed.");
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(command.OperatorTxnId) || command.OperatorTxnId.Length > 64)
        {
            errors["operatorTxnId"] = ["operatorTxnId is required."];
        }

        if (command.TopupId == Guid.Empty)
        {
            errors["topupId"] = ["topupId is required."];
        }

        if (command.Status is not ("SUCCESS" or "FAILED"))
        {
            errors["status"] = ["status must be SUCCESS or FAILED."];
        }

        if (command.EventSeq < 1)
        {
            errors["eventSeq"] = ["eventSeq must be a positive integer."];
        }

        if (errors.Count > 0)
        {
            return ApiResult.Problem(400, ErrorCodes.ValidationFailed, "The request was invalid.", errors: errors);
        }

        var result = await _store.ApplyCallbackAsync(
            command.TopupId,
            command.Status!,
            command.EventSeq,
            command.OperatorTxnId!.Trim(),
            _clock.GetUtcNow().UtcDateTime,
            cancellationToken);

        if (result.Kind == CallbackWriteKind.NotFound)
        {
            return ApiResult.Problem(404, ErrorCodes.NotFound, "Top-up was not found.");
        }

        _logger.LogInformation(
            "Operator callback for top-up {TopupId} seq {EventSeq} disposition {Disposition}",
            command.TopupId,
            command.EventSeq,
            result.Kind);

        var name = result.Kind switch
        {
            CallbackWriteKind.Applied => "applied",
            CallbackWriteKind.Duplicate => "duplicate",
            CallbackWriteKind.Late => "late",
            _ => "ignored"
        };
        return ApiResult.Json(200, new { result = name });
    }

    private static bool FixedSecretEquals(string expected, string? provided)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided ?? string.Empty);
        if (a.Length != b.Length)
        {
            CryptographicOperations.FixedTimeEquals(a, a);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed record ExpirePendingTopupsCommand(int BatchSize) : IRequest<int>;

public sealed class ExpirePendingTopupsHandler : IRequestHandler<ExpirePendingTopupsCommand, int>
{
    private readonly IFinancialWriteStore _store;
    private readonly TopupTiming _timing;
    private readonly TimeProvider _clock;

    public ExpirePendingTopupsHandler(IFinancialWriteStore store, TopupTiming timing, TimeProvider clock)
    {
        _store = store;
        _timing = timing;
        _clock = clock;
    }

    public Task<int> Handle(ExpirePendingTopupsCommand command, CancellationToken cancellationToken) =>
        _store.ExpirePendingAsync(_clock.GetUtcNow().UtcDateTime, _timing.ExpiryAfter, command.BatchSize, cancellationToken);
}

public sealed record ManualCreditCommand(Guid PartnerId, long AmountFils, string Reason, string? CorrelationId) : IRequest;

public sealed class ManualCreditHandler : IRequestHandler<ManualCreditCommand>
{
    private readonly IFinancialWriteStore _store;
    private readonly TimeProvider _clock;

    public ManualCreditHandler(IFinancialWriteStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public async Task Handle(ManualCreditCommand command, CancellationToken cancellationToken)
    {
        if (command.AmountFils <= 0)
        {
            throw new InvalidOperationException("Manual credit amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 500)
        {
            throw new InvalidOperationException("A reason is required.");
        }

        await _store.CreditWalletAsync(
            command.PartnerId,
            command.AmountFils,
            command.Reason.Trim(),
            command.CorrelationId,
            _clock.GetUtcNow().UtcDateTime,
            cancellationToken);
    }
}

public sealed record CreatePartnerCommand(string Name, PartnerTier Tier, Guid? ParentPartnerId) : IRequest<CreatePartnerResult>;

public sealed record CreatePartnerResult(Guid Id, string ApiKey, string SecretPlaintext);

public sealed class CreatePartnerHandler : IRequestHandler<CreatePartnerCommand, CreatePartnerResult>
{
    private readonly ILedgerDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _clock;

    public CreatePartnerHandler(ILedgerDbContext db, ISecretProtector protector, TimeProvider clock)
    {
        _db = db;
        _protector = protector;
        _clock = clock;
    }

    public async Task<CreatePartnerResult> Handle(CreatePartnerCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Trim().Length > 200)
        {
            throw new InvalidOperationException("Partner name is required.");
        }

        if (command.ParentPartnerId is Guid parentId)
        {
            var parentOk = await _db.Partners.AnyAsync(
                p => p.Id == parentId && p.Tier == PartnerTier.Distributor,
                cancellationToken);
            if (!parentOk)
            {
                throw new InvalidOperationException("Parent must be an existing distributor.");
            }
        }

        var secret = "sk_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var apiKey = "ak_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var partnerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        _db.Partners.Add(new Domain.Entities.Partner
        {
            Id = partnerId,
            Name = command.Name.Trim(),
            Tier = command.Tier,
            Status = PartnerStatus.Active,
            ApiKey = apiKey,
            SecretProtected = _protector.Protect(secret),
            ParentPartnerId = command.ParentPartnerId,
            CreatedAtUtc = now
        });
        _db.Accounts.Add(new Domain.Entities.Account
        {
            Id = accountId,
            PartnerId = partnerId,
            AccountType = AccountTypes.PartnerWallet
        });
        _db.Wallets.Add(new Domain.Entities.Wallet
        {
            PartnerId = partnerId,
            AccountId = accountId,
            BalanceFils = 0
        });
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new CreatePartnerResult(partnerId, apiKey, secret);
    }
}

public sealed record SetPartnerStatusCommand(Guid PartnerId, PartnerStatus Status) : IRequest;

public sealed record EnqueueSettlementCommand(DateOnly BusinessDay) : IRequest<Guid>;

public sealed class EnqueueSettlementHandler : IRequestHandler<EnqueueSettlementCommand, Guid>
{
    private readonly IFinancialWriteStore _store;
    private readonly TimeProvider _clock;

    public EnqueueSettlementHandler(IFinancialWriteStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<Guid> Handle(EnqueueSettlementCommand command, CancellationToken cancellationToken) =>
        _store.EnqueueSettlementAsync(command.BusinessDay, _clock.GetUtcNow().UtcDateTime, cancellationToken);
}

public sealed class SetPartnerStatusHandler : IRequestHandler<SetPartnerStatusCommand>
{
    private readonly ILedgerDbContext _db;

    public SetPartnerStatusHandler(ILedgerDbContext db) => _db = db;

    public async Task Handle(SetPartnerStatusCommand command, CancellationToken cancellationToken)
    {
        var partner = await _db.Partners.SingleAsync(p => p.Id == command.PartnerId, cancellationToken);
        partner.Status = command.Status;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
