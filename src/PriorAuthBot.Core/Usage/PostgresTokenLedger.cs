using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace PriorAuthBot.Core.Usage;

/// <summary>
/// Chapter 14: the token ledger in PostgreSQL, shared by every replica of the API. Chapter 13's
/// <see cref="InMemoryTokenLedger"/> counts in one process, so behind a load balancer with three
/// replicas each user would get three daily budgets and the spend alert would fire three times.
/// </summary>
/// <remarks>
/// One row per user per UTC day, updated with an atomic upsert, so concurrent charges from different
/// replicas add up instead of overwriting each other. A spend alert is a row with a primary key on
/// (day, threshold): the replica whose insert succeeds raises it, and every other one sees a conflict.
/// Every call is asynchronous. A first version used Npgsql's synchronous API, and the concurrency test
/// (sixty charges from three replicas at once) timed out now and then: each charge blocked a
/// thread-pool thread while it waited for the row lock, and the pool could not add threads fast enough.
/// </remarks>
public sealed class PostgresTokenLedger : ITokenLedger
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS priorauthbot_token_usage (
            day            date            NOT NULL,
            user_id        text            NOT NULL,
            input_tokens   bigint          NOT NULL DEFAULT 0,
            output_tokens  bigint          NOT NULL DEFAULT 0,
            model_calls    integer         NOT NULL DEFAULT 0,
            cost           numeric(18, 8)  NOT NULL DEFAULT 0,
            PRIMARY KEY (day, user_id)
        );
        CREATE TABLE IF NOT EXISTS priorauthbot_spend_alerts (
            day        date              NOT NULL,
            threshold  double precision  NOT NULL,
            raised_at  timestamptz       NOT NULL DEFAULT now(),
            PRIMARY KEY (day, threshold)
        );
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IOptionsMonitor<UsageOptions> _options;
    private readonly TimeProvider _time;
    private readonly Lazy<Task> _schema;

    public PostgresTokenLedger(NpgsqlDataSource dataSource, IOptionsMonitor<UsageOptions> options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        _dataSource = dataSource;
        _options = options;
        _time = time ?? TimeProvider.System;
        _schema = new Lazy<Task>(async () =>
        {
            if (_options.CurrentValue.CreateSchema)
            {
                await using var command = _dataSource.CreateCommand(Schema);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        });
    }

    public DateTimeOffset ResetsAt => new(_time.GetUtcNow().UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    private DateOnly Day => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    public async ValueTask<UserUsage> ForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await _schema.Value.ConfigureAwait(false);
        var day = Day;
        await using var command = _dataSource.CreateCommand(
            "SELECT input_tokens, output_tokens, model_calls, cost FROM priorauthbot_token_usage WHERE day = $1 AND user_id = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = day, NpgsqlDbType = NpgsqlDbType.Date });
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new UserUsage(userId, day, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetDecimal(3))
            : new UserUsage(userId, day, 0, 0, 0, 0m);
    }

    public async ValueTask<DailyUsage> TodayAsync(CancellationToken cancellationToken = default)
    {
        await _schema.Value.ConfigureAwait(false);
        var day = Day;
        await using var command = _dataSource.CreateCommand("""
            SELECT coalesce(sum(input_tokens), 0)::bigint, coalesce(sum(output_tokens), 0)::bigint,
                   coalesce(sum(model_calls), 0)::integer, coalesce(sum(cost), 0), count(*)::integer
            FROM priorauthbot_token_usage WHERE day = $1
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = day, NpgsqlDbType = NpgsqlDbType.Date });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DailyUsage(day, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetDecimal(3), reader.GetInt32(4));
    }

    public async ValueTask<IReadOnlyList<double>> ChargeAsync(string userId, long inputTokens, long outputTokens, decimal cost, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await _schema.Value.ConfigureAwait(false);
        var day = Day;
        var settings = _options.CurrentValue;

        await using var batch = _dataSource.CreateBatch();

        // 1. Add the charge to the user's row for today, creating it if this is their first call.
        var charge = new NpgsqlBatchCommand("""
            INSERT INTO priorauthbot_token_usage AS u (day, user_id, input_tokens, output_tokens, model_calls, cost)
            VALUES ($1, $2, $3, $4, 1, $5)
            ON CONFLICT (day, user_id) DO UPDATE SET
                input_tokens  = u.input_tokens + EXCLUDED.input_tokens,
                output_tokens = u.output_tokens + EXCLUDED.output_tokens,
                model_calls   = u.model_calls + 1,
                cost          = u.cost + EXCLUDED.cost
            """);
        charge.Parameters.Add(new NpgsqlParameter { Value = day, NpgsqlDbType = NpgsqlDbType.Date });
        charge.Parameters.Add(new NpgsqlParameter { Value = userId });
        charge.Parameters.Add(new NpgsqlParameter { Value = inputTokens });
        charge.Parameters.Add(new NpgsqlParameter { Value = outputTokens });
        charge.Parameters.Add(new NpgsqlParameter { Value = cost });
        batch.BatchCommands.Add(charge);

        // 2. Claim every threshold today's total has now crossed. Only the first claim of each succeeds.
        if (settings.DailySpendAlertUsd > 0)
        {
            var alerts = new NpgsqlBatchCommand("""
                INSERT INTO priorauthbot_spend_alerts (day, threshold)
                SELECT $1, t FROM unnest($2::double precision[]) AS t
                WHERE (SELECT coalesce(sum(cost), 0) FROM priorauthbot_token_usage WHERE day = $1) >= $3 * t::numeric
                ON CONFLICT (day, threshold) DO NOTHING
                RETURNING threshold
                """);
            alerts.Parameters.Add(new NpgsqlParameter { Value = day, NpgsqlDbType = NpgsqlDbType.Date });
            alerts.Parameters.Add(new NpgsqlParameter { Value = settings.EffectiveThresholds.ToArray() });
            alerts.Parameters.Add(new NpgsqlParameter { Value = settings.DailySpendAlertUsd });
            batch.BatchCommands.Add(alerts);
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var crossed = new List<double>();
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                crossed.Add(reader.GetDouble(0));
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        crossed.Sort();
        return crossed;
    }
}
