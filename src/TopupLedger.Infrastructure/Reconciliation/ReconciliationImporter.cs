using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using TopupLedger.Application.Persistence;
using TopupLedger.Infrastructure.Persistence;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Money;
using TopupLedger.Domain.Msisdn;

namespace TopupLedger.Infrastructure.Reconciliation;

public interface IReconciliationImporter
{
    Task<Guid> ImportAsync(Stream content, string fileName, CancellationToken cancellationToken);
}

public sealed class ReconciliationImporter : IReconciliationImporter
{
    private readonly LedgerDbContext _db;

    public ReconciliationImporter(LedgerDbContext db) => _db = db;

    public async Task<Guid> ImportAsync(Stream content, string fileName, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), "topupledger-" + Guid.NewGuid().ToString("N") + ".csv");
        string hash;
        try
        {
            await using (var output = File.Create(temp))
            {
                using var sha = SHA256.Create();
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            var existing = await _db.ReconciliationRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.FileSha256 == hash, cancellationToken);
            if (existing is not null)
            {
                return existing.Id;
            }

            var run = new ReconciliationRun
            {
                Id = Guid.NewGuid(),
                FileSha256 = hash,
                FileName = Path.GetFileName(fileName),
                Status = ReconciliationStatuses.Ready,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.ReconciliationRuns.Add(run);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                var raced = await _db.ReconciliationRuns.AsNoTracking().SingleAsync(r => r.FileSha256 == hash, cancellationToken);
                return raced.Id;
            }

            var duplicates = await LoadLinesAsync(temp, run.Id, cancellationToken);
            await SummariseAsync(run.Id, duplicates, cancellationToken);
            await _db.Entry(run).ReloadAsync(cancellationToken);
            return run.Id;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private async Task<int> LoadLinesAsync(string path, Guid runId, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var batch = new List<ParsedLine>(200);
        var duplicates = 0;
        var sawHeader = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = SplitCsv(line);
            if (fields.Count == 0)
            {
                continue;
            }

            if (fields[0].Trim().Equals("operator_txn_id", StringComparison.OrdinalIgnoreCase))
            {
                sawHeader = true;
                continue;
            }

            if (!sawHeader)
            {
                throw new InvalidOperationException("The CSV is missing a header row.");
            }

            if (fields.Count < 5)
            {
                continue;
            }

            var txn = fields[0].Trim();
            if (txn.Length == 0 || txn.Length > 64)
            {
                continue;
            }

            var msisdn = fields[1].Trim();
            if (MsisdnNormalizer.TryNormalize(msisdn, out var normalized, out _))
            {
                msisdn = normalized.Value;
            }

            var amountText = fields[2].Trim().Replace(",", string.Empty, StringComparison.Ordinal);
            if (!Fils.TryParseAed(amountText, out var fils, out _))
            {
                if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var aed))
                {
                    continue;
                }

                fils = (long)decimal.Round(aed * 100m, 0, MidpointRounding.AwayFromZero);
            }

            if (!DateTime.TryParseExact(fields[4].Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            {
                continue;
            }

            batch.Add(new ParsedLine(txn, msisdn, fils, fields[3].Trim().ToUpperInvariant(), when));
            if (batch.Count == 200)
            {
                duplicates += await InsertBatchAsync(runId, batch, cancellationToken);
                batch.Clear();
            }
        }

        if (!sawHeader)
        {
            throw new InvalidOperationException("The CSV is missing a header row.");
        }

        if (batch.Count > 0)
        {
            duplicates += await InsertBatchAsync(runId, batch, cancellationToken);
        }

        return duplicates;
    }

    private async Task<int> InsertBatchAsync(Guid runId, List<ParsedLine> batch, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder("INSERT IGNORE INTO reconciliation_lines (run_id, operator_txn_id, msisdn, amount_fils, status, event_time_dubai) VALUES ");
        var parameters = new List<MySqlParameter> { new("@runId", runId) };
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(',');
            }

            sql.Append($"(@runId, @txn{i}, @msisdn{i}, @amt{i}, @status{i}, @when{i})");
            parameters.Add(new MySqlParameter($"@txn{i}", batch[i].Txn));
            parameters.Add(new MySqlParameter($"@msisdn{i}", batch[i].Msisdn));
            parameters.Add(new MySqlParameter($"@amt{i}", batch[i].AmountFils));
            parameters.Add(new MySqlParameter($"@status{i}", batch[i].Status));
            parameters.Add(new MySqlParameter($"@when{i}", batch[i].EventTime));
        }

        var connection = (MySqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddRange(parameters.ToArray());
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        return batch.Count - inserted;
    }

    private async Task SummariseAsync(Guid runId, int duplicates, CancellationToken cancellationToken)
    {
        var connection = (MySqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await ExecuteAsync(connection, """
            INSERT INTO reconciliation_findings (run_id, category, operator_txn_id, topup_id, detail)
            SELECT @runId, 'MISSING_IN_OURS', l.operator_txn_id, NULL, 'Operator row has no matching top-up.'
            FROM reconciliation_lines l
            LEFT JOIN topups t ON t.operator_txn_id = l.operator_txn_id
            WHERE l.run_id = @runId AND t.id IS NULL
            """, runId, cancellationToken);

        await ExecuteAsync(connection, """
            INSERT INTO reconciliation_findings (run_id, category, operator_txn_id, topup_id, detail)
            SELECT @runId, 'AMOUNT_MISMATCH', l.operator_txn_id, t.id,
                   CONCAT('Operator amount differs from face value ', t.face_value_fils, ' fils.')
            FROM reconciliation_lines l
            INNER JOIN topups t ON t.operator_txn_id = l.operator_txn_id
            WHERE l.run_id = @runId AND t.face_value_fils <> l.amount_fils
            """, runId, cancellationToken);

        await ExecuteAsync(connection, """
            INSERT INTO reconciliation_findings (run_id, category, operator_txn_id, topup_id, detail)
            SELECT @runId, 'STATUS_MISMATCH', l.operator_txn_id, t.id,
                   CONCAT('Operator status ', l.status, ' does not match ', t.status, '.')
            FROM reconciliation_lines l
            INNER JOIN topups t ON t.operator_txn_id = l.operator_txn_id
            WHERE l.run_id = @runId
              AND NOT (
                    (l.status = 'SUCCESS' AND t.status = 'SUCCESS')
                 OR (l.status = 'FAILED' AND t.status = 'FAILED'))
            """, runId, cancellationToken);

        await ExecuteAsync(connection, """
            INSERT INTO reconciliation_findings (run_id, category, operator_txn_id, topup_id, detail)
            SELECT @runId, 'MISSING_IN_OPERATOR', t.operator_txn_id, t.id, 'Top-up in the file window is absent from the operator export.'
            FROM topups t
            JOIN (
                SELECT MIN(event_time_dubai) AS min_at, MAX(event_time_dubai) AS max_at
                FROM reconciliation_lines WHERE run_id = @runId
            ) w ON w.min_at IS NOT NULL
            LEFT JOIN reconciliation_lines l ON l.run_id = @runId AND l.operator_txn_id = t.operator_txn_id
            WHERE t.status IN ('SUCCESS', 'FAILED', 'EXPIRED')
              AND l.id IS NULL
              AND DATE_ADD(COALESCE(t.completed_at_utc, t.created_at_utc), INTERVAL 4 HOUR) BETWEEN w.min_at AND w.max_at
            """, runId, cancellationToken);

        await ExecuteAsync(connection, """
            UPDATE reconciliation_runs r
            SET matched_count = (
                    SELECT COUNT(*) FROM reconciliation_lines l
                    INNER JOIN topups t ON t.operator_txn_id = l.operator_txn_id
                    WHERE l.run_id = r.id
                      AND t.face_value_fils = l.amount_fils
                      AND t.msisdn = l.msisdn
                      AND ((l.status = 'SUCCESS' AND t.status = 'SUCCESS') OR (l.status = 'FAILED' AND t.status = 'FAILED'))
                ),
                missing_in_ours_count = (SELECT COUNT(*) FROM reconciliation_findings f WHERE f.run_id = r.id AND f.category = 'MISSING_IN_OURS'),
                missing_in_operator_count = (SELECT COUNT(*) FROM reconciliation_findings f WHERE f.run_id = r.id AND f.category = 'MISSING_IN_OPERATOR'),
                amount_mismatch_count = (SELECT COUNT(*) FROM reconciliation_findings f WHERE f.run_id = r.id AND f.category = 'AMOUNT_MISMATCH'),
                status_mismatch_count = (SELECT COUNT(*) FROM reconciliation_findings f WHERE f.run_id = r.id AND f.category = 'STATUS_MISMATCH'),
                duplicate_row_count = @duplicates
            WHERE r.id = @runId
            """, runId, cancellationToken, duplicates);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, Guid runId, CancellationToken cancellationToken, int? duplicates = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new MySqlParameter("@runId", runId));
        if (duplicates is not null)
        {
            command.Parameters.Add(new MySqlParameter("@duplicates", duplicates.Value));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (c is not '\r')
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private sealed record ParsedLine(string Txn, string Msisdn, long AmountFils, string Status, DateTime EventTime);
}
