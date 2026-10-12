using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MySqlConnector;
using Serilog;
using TopupLedger.Api.Http;
using TopupLedger.Api.Hosting;
using TopupLedger.Api.OpenApi;
using TopupLedger.Api.Security;
using TopupLedger.Application;
using TopupLedger.Application.Api;
using TopupLedger.Infrastructure;
using TopupLedger.Infrastructure.Demo;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHttpClient("operator-callback", (sp, client) =>
    {
        var baseUrl = sp.GetRequiredService<IConfiguration>()["OperatorSimulator:CallbackBaseUrl"] ?? "http://127.0.0.1:8080";
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    });
    builder.Services.AddHealthChecks().AddCheck<MySqlReadyCheck>("mysql", tags: ["ready"]);
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new() { Title = "TopupLedger Partner API", Version = "v1" });
        options.OperationFilter<HmacOperationFilter>();
    });
    builder.Services.AddHostedService<ExpiryWorker>();
    builder.Services.AddHostedService<SettlementWorker>();
    builder.Services.AddHostedService<OperatorSimulatorWorker>();

    var app = builder.Build();
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    });
    app.Use(async (context, next) =>
    {
        var incoming = context.Request.Headers["X-Correlation-Id"].ToString();
        var correlationId = string.IsNullOrWhiteSpace(incoming) ? Guid.NewGuid().ToString("N") : incoming;
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next();
        }
    });
    app.UseExceptionHandler(handler => handler.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
        logger.LogError(feature?.Error, "Unhandled API exception. Correlation {CorrelationId}", context.Response.Headers["X-Correlation-Id"].ToString());
        await ApiResultHttp.WriteAsync(
            context.Response,
            ApiResult.Problem(500, "INTERNAL", "An unexpected error occurred."),
            context.RequestAborted);
    }));

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseMiddleware<PartnerRequestMiddleware>();
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
    app.MapControllers();

    if (app.Configuration.GetValue("Seed:OnStartup", true))
    {
        await DemoSeed.EnsureAsync(app.Services, CancellationToken.None);
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API host terminated.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public sealed class MySqlReadyCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly IConfiguration _configuration;

    public MySqlReadyCheck(IConfiguration configuration) => _configuration = configuration;

    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var cs = _configuration.GetConnectionString("TopupLedger");
            await using var connection = new MySqlConnection(cs);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("MySQL is not reachable.", ex);
        }
    }
}

public partial class Program;
