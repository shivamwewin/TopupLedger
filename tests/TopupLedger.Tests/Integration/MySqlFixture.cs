using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Testcontainers.MySql;

namespace TopupLedger.Tests.Integration;

public sealed class MySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithImage("mysql:8.0")
        .WithDatabase("topupledger")
        .WithUsername("topup")
        .WithPassword("topup")
        .WithCommand("--log-bin-trust-function-creators=1", "--max-connections=400")
        .Build();

    public TopupApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString() + ";GuidFormat=Char36;SslMode=None;AllowPublicKeyRetrieval=True;Maximum Pool Size=300";
        await MySqlScript.ExecuteAsync(connectionString, RepoFile("db", "schema.sql"));
        if (!connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";Maximum Pool Size=250";
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__TopupLedger", connectionString);
        Environment.SetEnvironmentVariable("DataProtection__KeyBase64", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        Environment.SetEnvironmentVariable("Operators__EtisalatToken", "dev-etisalat-token");
        Environment.SetEnvironmentVariable("Operators__DuToken", "dev-du-token");
        Environment.SetEnvironmentVariable("LedgerRules__Variant", "V1");
        Environment.SetEnvironmentVariable("Seed__OnStartup", "true");
        Environment.SetEnvironmentVariable("Workers__Expiry", "false");
        Environment.SetEnvironmentVariable("Workers__Settlement", "false");
        Environment.SetEnvironmentVariable("OperatorSimulator__Enabled", "false");
        Environment.SetEnvironmentVariable("RateLimit__PermitsPerSecond", "5000");
        Environment.SetEnvironmentVariable("RateLimit__Burst", "5000");
        Factory = new TopupApiFactory(connectionString);
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    public static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "db", "schema.sql")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("schema.sql was not found.");
        }

        return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }
}

public sealed class TopupApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public TopupApiFactory(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("environment", "Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TopupLedger"] = _connectionString,
                ["DataProtection:KeyBase64"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                ["Operators:EtisalatToken"] = "dev-etisalat-token",
                ["Operators:DuToken"] = "dev-du-token",
                ["LedgerRules:Variant"] = "V1",
                ["Seed:OnStartup"] = "true",
                ["Workers:Expiry"] = "false",
                ["Workers:Settlement"] = "false",
                ["OperatorSimulator:Enabled"] = "false",
                ["RateLimit:PermitsPerSecond"] = "5000",
                ["RateLimit:Burst"] = "5000"
            });
        });
    }
}

public static class MySqlScript
{
    public static async Task ExecuteAsync(string connectionString, string path)
    {
        var text = await File.ReadAllTextAsync(path);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var delimiter = ";";
        var buffer = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
            {
                delimiter = trimmed["DELIMITER".Length..].Trim();
                continue;
            }

            buffer.AppendLine(raw);
            var current = buffer.ToString().Trim();
            if (!current.EndsWith(delimiter, StringComparison.Ordinal))
            {
                continue;
            }

            var statement = current[..^delimiter.Length].Trim();
            buffer.Clear();
            if (statement.Length == 0)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>
{
    public const string Name = "mysql";
}
