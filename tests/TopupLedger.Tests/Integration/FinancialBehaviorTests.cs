using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Security;
using TopupLedger.Application.Topups;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Partners;
using TopupLedger.Infrastructure.Demo;
using TopupLedger.Infrastructure.Persistence;
using TopupLedger.Infrastructure.Reconciliation;

namespace TopupLedger.Tests.Integration;

[Collection(MySqlCollection.Name)]
public sealed class FinancialBehaviorTests
{
    private readonly MySqlFixture _fx;

    public FinancialBehaviorTests(MySqlFixture fx) => _fx = fx;

    [Fact]
    public async Task Successful_topup_debits_once_and_replay_is_byte_for_byte()
    {
        var partner = await NewRetailerAsync(50_000);
        var body = Body("0501234567", "10.00", "ETISALAT", "RTL-1");
        var key = Guid.NewGuid();
        var first = await PostTopupAsync(partner, key, body);
        var second = await PostTopupAsync(partner, key, body);
        Assert.Equal(202, first.Status);
        Assert.Equal(202, second.Status);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(50_000 - 980, await BalanceAsync(partner.Id));
        Assert.Equal(1, await TopupCountAsync(partner.Id));
    }

    [Fact]
    public async Task Insufficient_funds_writes_only_the_idempotency_record()
    {
        var partner = await NewRetailerAsync(10);
        var body = Body("0501234567", "10.00", "ETISALAT", "RTL-poor");
        var key = Guid.NewGuid();
        var response = await PostTopupAsync(partner, key, body);
        Assert.Equal(402, response.Status);
        Assert.Contains("INSUFFICIENT_FUNDS", Encoding.UTF8.GetString(response.Bytes));
        Assert.Equal(0, await TopupCountAsync(partner.Id));
        Assert.Equal(10, await BalanceAsync(partner.Id));
        var replay = await PostTopupAsync(partner, key, body);
        Assert.Equal(response.Bytes, replay.Bytes);
    }

    [Fact]
    public async Task Different_body_with_the_same_key_is_rejected()
    {
        var partner = await NewRetailerAsync(50_000);
        var key = Guid.NewGuid();
        var first = await PostTopupAsync(partner, key, Body("0501234567", "10.00", "ETISALAT", "RTL-body"));
        var second = await PostTopupAsync(partner, key, Body("0501234567", "11.00", "ETISALAT", "RTL-body"));
        Assert.Equal(202, first.Status);
        Assert.Equal(422, second.Status);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", Encoding.UTF8.GetString(second.Bytes));
        Assert.Equal(1, await TopupCountAsync(partner.Id));
    }

    [Fact]
    public async Task Duplicate_client_ref_does_not_debit_twice()
    {
        var partner = await NewRetailerAsync(50_000);
        var first = await PostTopupAsync(partner, Guid.NewGuid(), Body("0501234567", "10.00", "ETISALET".Replace("ETISALET", "ETISALAT"), "RTL-dup"));
        var second = await PostTopupAsync(partner, Guid.NewGuid(), Body("0501234567", "10.00", "ETISALAT", "RTL-dup"));
        Assert.Equal(202, first.Status);
        Assert.Equal(409, second.Status);
        Assert.Contains("DUPLICATE_CLIENT_REF", Encoding.UTF8.GetString(second.Bytes));
        Assert.Equal(50_000 - 980, await BalanceAsync(partner.Id));
    }

    [Fact]
    public async Task Invalid_signature_and_clock_skew_are_rejected()
    {
        var body = Body("0501234567", "10.00", "ETISALAT", "RTL-auth");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/topups")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Api-Key", DemoSeed.RetailerApiKey);
        request.Headers.TryAddWithoutValidation("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        request.Headers.TryAddWithoutValidation("X-Signature", "abcd");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        var badSig = await _fx.Client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, badSig.StatusCode);
        Assert.Contains("AUTH_INVALID_SIGNATURE", await badSig.Content.ReadAsStringAsync());

        var skew = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/topups") { Content = new ByteArrayContent(body) };
        Sign(skew, DemoSeed.RetailerSecret, DemoSeed.RetailerApiKey, body, Guid.NewGuid(), "1");
        var skewResponse = await _fx.Client.SendAsync(skew);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, skewResponse.StatusCode);
        Assert.Contains("AUTH_CLOCK_SKEW", await skewResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Operator_prefix_mismatch_is_validation_failed()
    {
        var partner = await NewRetailerAsync(50_000);
        var response = await PostTopupAsync(partner, Guid.NewGuid(), Body("0559123456", "10.00", "ETISALAT", "RTL-prefix"));
        Assert.Equal(400, response.Status);
        Assert.Contains("VALIDATION_FAILED", Encoding.UTF8.GetString(response.Bytes));
        Assert.Equal(50_000, await BalanceAsync(partner.Id));
    }

    [Fact]
    public async Task Suspended_partner_is_forbidden()
    {
        var body = Body("0501234567", "10.00", "ETISALAT", "RTL-susp");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/topups") { Content = new ByteArrayContent(body) };
        Sign(request, DemoSeed.SuspendedSecret, DemoSeed.SuspendedApiKey, body, Guid.NewGuid(), null);
        var response = await _fx.Client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("PARTNER_SUSPENDED", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_same_key_creates_one_topup()
    {
        var partner = await NewRetailerAsync(50_000);
        var key = Guid.NewGuid();
        var body = Body("0501234567", "10.00", "ETISALAT", "RTL-race");
        var tasks = Enumerable.Range(0, 12).Select(_ => PostTopupAsync(partner, key, body)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Contains(results, r => r.Status == 202);
        Assert.True(results.All(r => r.Status is 202 or 409), string.Join(" | ", results.Select(r => r.Status + " " + Encoding.UTF8.GetString(r.Bytes))));
        Assert.Equal(1, await TopupCountAsync(partner.Id));
        Assert.Equal(50_000 - 980, await BalanceAsync(partner.Id));
    }

    [Fact]
    public async Task Two_hundred_parallel_debits_cannot_overspend()
    {
        const int succeed = 150;
        const int total = 200;
        const long debit = 980;
        var partner = await NewRetailerAsync(succeed * debit);
        var tasks = Enumerable.Range(0, total).Select(i =>
            PostTopupAsync(partner, Guid.NewGuid(), Body("0501234567", "10.00", "ETISALAT", "C" + i))).ToArray();
        var results = await Task.WhenAll(tasks);
        var breakdown = string.Join(", ", results.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count()));
        var unexpected = results.FirstOrDefault(r => r.Status != 202 && r.Status != 402);
        var sample = unexpected is null ? string.Empty : Encoding.UTF8.GetString(unexpected.Bytes);
        Assert.True(results.Count(r => r.Status == 202) == succeed, breakdown + " " + sample);
        Assert.Equal(total - succeed, results.Count(r => r.Status == 402));
        Assert.Equal(0, await BalanceAsync(partner.Id));
        await AssertPartnerBookBalancedAsync(partner.Id);
    }

    [Fact]
    public async Task Callbacks_are_idempotent_out_of_order_safe_and_refund_once()
    {
        var partner = await NewRetailerAsync(50_000);
        var created = await PostTopupAsync(partner, Guid.NewGuid(), Body("0501234567", "10.00", "ETISALAT", "RTL-cb"));
        var id = JsonDocument.Parse(created.Bytes).RootElement.GetProperty("id").GetGuid();

        var distributorBefore = await BalanceAsync(DemoSeed.DistributorId);
        var success = await CallbackAsync("etisalat", id, "SUCCESS", 2, "ETS-1");
        var lateLower = await CallbackAsync("etisalat", id, "FAILED", 1, "ETS-0");
        var duplicate = await CallbackAsync("etisalat", id, "SUCCESS", 2, "ETS-1");
        Assert.Equal(System.Net.HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, lateLower.StatusCode);
        Assert.Contains("duplicate", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal("SUCCESS", await StatusAsync(id));
        Assert.Equal(distributorBefore + 8, await BalanceAsync(DemoSeed.DistributorId));
        Assert.Equal(50_000 - 980, await BalanceAsync(partner.Id));

        var failedPartner = await NewRetailerAsync(50_000);
        var failedCreate = await PostTopupAsync(failedPartner, Guid.NewGuid(), Body("0501234567", "10.00", "ETISALAT", "RTL-fail"));
        var failedId = JsonDocument.Parse(failedCreate.Bytes).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await CallbackAsync("etisalat", failedId, "FAILED", 1, "ETS-F")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await CallbackAsync("etisalat", failedId, "FAILED", 1, "ETS-F")).StatusCode);
        Assert.Equal("FAILED", await StatusAsync(failedId));
        Assert.Equal(50_000, await BalanceAsync(failedPartner.Id));
        await AssertPartnerBookBalancedAsync(failedPartner.Id);
    }

    [Fact]
    public async Task Late_success_after_expiry_does_not_debit_again()
    {
        var partner = await NewRetailerAsync(50_000);
        var created = await PostTopupAsync(partner, Guid.NewGuid(), Body("0501234567", "10.00", "ETISALAT", "RTL-exp"));
        var id = JsonDocument.Parse(created.Bytes).RootElement.GetProperty("id").GetGuid();
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE topups SET created_at_utc = {DateTime.UtcNow.AddMinutes(-10)} WHERE id = {id}");
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var expired = await mediator.Send(new ExpirePendingTopupsCommand(20));
            Assert.True(expired >= 1);
        }

        Assert.Equal("EXPIRED", await StatusAsync(id));
        Assert.Equal(50_000, await BalanceAsync(partner.Id));
        var late = await CallbackAsync("etisalat", id, "SUCCESS", 1, "ETS-LATE");
        Assert.Equal(System.Net.HttpStatusCode.OK, late.StatusCode);
        Assert.Contains("late", await late.Content.ReadAsStringAsync());
        Assert.Equal("EXPIRED", await StatusAsync(id));
        Assert.Equal(50_000, await BalanceAsync(partner.Id));
        await using var check = _fx.Factory.Services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<ILedgerDbContext>();
        Assert.Equal(1, await context.LateCallbacks.CountAsync(c => c.TopupId == id));
    }

    [Fact]
    public async Task Reconciliation_reupload_does_not_create_a_second_run()
    {
        var partner = await NewRetailerAsync(50_000);
        var created = await PostTopupAsync(partner, Guid.NewGuid(), Body("0501234567", "25.00", "ETISALAT", "RTL-recon"));
        var id = JsonDocument.Parse(created.Bytes).RootElement.GetProperty("id").GetGuid();
        await CallbackAsync("etisalat", id, "SUCCESS", 1, "ETS-RECON-1");
        string dubai;
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
            var completed = await db.Topups.Where(t => t.Id == id).Select(t => t.CompletedAtUtc).SingleAsync();
            dubai = completed!.Value.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");
        }

        var csv = "operator_txn_id,msisdn,amount_aed,status,event_time_dubai\r\n" +
                  $"ETS-RECON-1,971501234567,25.00,SUCCESS,{dubai}\r\n" +
                  "operator_txn_id,msisdn,amount_aed,status,event_time_dubai\r\n" +
                  $"ETS-MISSING,971501234567,\"1,000.00\",FAILED,{dubai}\r\n" +
                  $"ETS-RECON-1,971501234567,25.00,SUCCESS,{dubai}\r\n\r\n";
        await using var scope2 = _fx.Factory.Services.CreateAsyncScope();
        var importer = scope2.ServiceProvider.GetRequiredService<IReconciliationImporter>();
        var first = await importer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes("\uFEFF" + csv)), "ops.csv", CancellationToken.None);
        var second = await importer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes("\uFEFF" + csv)), "ops.csv", CancellationToken.None);
        Assert.Equal(first, second);
        var db2 = scope2.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var run = await db2.ReconciliationRuns.SingleAsync(r => r.Id == first);
        Assert.True(run.MatchedCount >= 1, $"matched={run.MatchedCount} missingOurs={run.MissingInOursCount} dup={run.DuplicateRowCount}");
        Assert.True(run.MissingInOursCount >= 1);
        Assert.True(run.DuplicateRowCount >= 1);
    }

    [Fact]
    public async Task Ledger_rows_cannot_be_updated()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var id = await db.LedgerEntries.Select(e => e.Id).FirstAsync();
        var ex = await Assert.ThrowsAsync<MySqlException>(async () =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ledger_entries SET amount_fils = 0 WHERE id = {id}"));
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CreatedPartner> NewRetailerAsync(long openingFils)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var created = await mediator.Send(new CreatePartnerCommand("Shop " + Guid.NewGuid().ToString("N")[..8], PartnerTier.Retailer, DemoSeed.DistributorId));
        if (openingFils > 0)
        {
            await mediator.Send(new ManualCreditCommand(created.Id, openingFils, "test float", "test"));
        }

        return new CreatedPartner(created.Id, created.ApiKey, created.SecretPlaintext);
    }

    private async Task<RawResponse> PostTopupAsync(CreatedPartner partner, Guid key, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/topups")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Sign(request, partner.Secret, partner.ApiKey, body, key, null);
        var response = await _fx.Client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new RawResponse((int)response.StatusCode, bytes);
    }

    private async Task<HttpResponseMessage> CallbackAsync(string op, Guid topupId, string status, int seq, string txn)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operator/callbacks/{op}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { operatorTxnId = txn, topupId, status, eventSeq = seq }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Operator-Token", "dev-etisalat-token");
        return await _fx.Client.SendAsync(request);
    }

    private static void Sign(HttpRequestMessage request, string secret, string apiKey, byte[] body, Guid key, string? timestampOverride)
    {
        var timestamp = timestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var canonical = CanonicalSigner.Canonical("POST", "/api/v1/partner/topups", timestamp, key.ToString(), CanonicalSigner.HashBody(body));
        var signature = Convert.ToHexString(CanonicalSigner.Sign(secret, canonical)).ToLowerInvariant();
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Signature", signature);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString());
    }

    private static byte[] Body(string msisdn, string amount, string op, string clientRef) =>
        Encoding.UTF8.GetBytes($"{{\"msisdn\":\"{msisdn}\",\"amount\":\"{amount}\",\"operator\":\"{op}\",\"clientRef\":\"{clientRef}\"}}");

    private async Task<long> BalanceAsync(Guid partnerId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ILedgerDbContext>();
        return await db.Wallets.Where(w => w.PartnerId == partnerId).Select(w => w.BalanceFils).SingleAsync();
    }

    private async Task<int> TopupCountAsync(Guid partnerId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ILedgerDbContext>();
        return await db.Topups.CountAsync(t => t.PartnerId == partnerId);
    }

    private async Task<string> StatusAsync(Guid id)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await db.Topups.Where(t => t.Id == id).Select(t => t.Status).SingleAsync() switch
        {
            Domain.Topups.TopupStatus.Pending => "PENDING",
            Domain.Topups.TopupStatus.Success => "SUCCESS",
            Domain.Topups.TopupStatus.Failed => "FAILED",
            _ => "EXPIRED"
        };
    }

    private async Task AssertPartnerBookBalancedAsync(Guid partnerId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var wallet = await db.Wallets.SingleAsync(w => w.PartnerId == partnerId);
        var sum = await db.LedgerEntries.Where(e => e.AccountId == wallet.AccountId).SumAsync(e => e.AmountFils);
        Assert.Equal(wallet.BalanceFils, sum);
        var movements = await db.LedgerEntries.Where(e => e.TopupId != null && db.Topups.Any(t => t.Id == e.TopupId && t.PartnerId == partnerId))
            .GroupBy(e => e.TransactionId)
            .Select(g => g.Sum(x => x.AmountFils))
            .ToListAsync();
        Assert.All(movements, s => Assert.Equal(0, s));
    }

    private sealed record CreatedPartner(Guid Id, string ApiKey, string Secret);
    private sealed record RawResponse(int Status, byte[] Bytes);
}
