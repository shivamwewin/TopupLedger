using System.Net.Http.Json;
using MediatR;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Topups;
using TopupLedger.Domain.Msisdn;

namespace TopupLedger.Api.Hosting;

public sealed class ExpiryWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ExpiryWorker> _logger;
    private readonly bool _enabled;

    public ExpiryWorker(IServiceProvider services, IConfiguration configuration, ILogger<ExpiryWorker> logger)
    {
        _services = services;
        _logger = logger;
        _enabled = configuration.GetValue("Workers:Expiry", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var count = await mediator.Send(new ExpirePendingTopupsCommand(50), stoppingToken);
                if (count > 0)
                {
                    _logger.LogInformation("Expired {Count} top-ups with compensating ledger entries.", count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Expiry worker failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

public sealed class SettlementWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SettlementWorker> _logger;
    private readonly bool _enabled;

    public SettlementWorker(IServiceProvider services, IConfiguration configuration, ILogger<SettlementWorker> logger)
    {
        _services = services;
        _logger = logger;
        _enabled = configuration.GetValue("Workers:Settlement", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IFinancialWriteStore>();
                var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
                await store.TryRunNextSettlementAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Settlement worker failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

public sealed class OperatorSimulatorWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatorSimulatorWorker> _logger;

    public OperatorSimulatorWorker(IServiceProvider services, IHttpClientFactory http, IConfiguration configuration, ILogger<OperatorSimulatorWorker> logger)
    {
        _services = services;
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("OperatorSimulator:Enabled", true))
        {
            return;
        }

        var minDelay = _configuration.GetValue("OperatorSimulator:MinDelaySeconds", 1);
        var maxDelay = _configuration.GetValue("OperatorSimulator:MaxDelaySeconds", 15);
        var success = _configuration.GetValue("OperatorSimulator:SuccessPercent", 85);
        var failed = _configuration.GetValue("OperatorSimulator:FailedPercent", 10);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IFinancialWriteStore>();
                var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
                var tokens = scope.ServiceProvider.GetRequiredService<OperatorTokens>();
                var batch = await store.ClaimOperatorDispatchAsync(clock.GetUtcNow().UtcDateTime, 20, stoppingToken);
                foreach (var item in batch)
                {
                    _ = DispatchAsync(item, tokens, minDelay, maxDelay, success, failed, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Operator simulator failed to claim work.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task DispatchAsync(DispatchedTopup item, OperatorTokens tokens, int minDelay, int maxDelay, int successPercent, int failedPercent, CancellationToken cancellationToken)
    {
        try
        {
            var delay = Random.Shared.Next(minDelay, Math.Max(minDelay, maxDelay) + 1);
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
            var roll = Random.Shared.Next(100);
            if (roll >= successPercent + failedPercent)
            {
                _logger.LogInformation("Fake operator left top-up {TopupId} without a callback.", item.TopupId);
                return;
            }

            var status = roll < successPercent ? "SUCCESS" : "FAILED";
            var prefix = item.Operator == MobileOperator.Etisalat ? "ETS" : "DU";
            var token = item.Operator == MobileOperator.Etisalat ? tokens.Etisalat : tokens.Du;
            var client = _http.CreateClient("operator-callback");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/operator/callbacks/{MobileOperatorParser.ToRoute(item.Operator)}")
            {
                Content = JsonContent.Create(new
                {
                    operatorTxnId = prefix + "-" + Guid.NewGuid().ToString("N")[..12],
                    topupId = item.TopupId,
                    status,
                    eventSeq = 1
                })
            };
            request.Headers.TryAddWithoutValidation("X-Operator-Token", token);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Fake operator callback for {TopupId} returned {StatusCode}.", item.TopupId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fake operator callback for {TopupId} failed.", item.TopupId);
        }
    }
}
