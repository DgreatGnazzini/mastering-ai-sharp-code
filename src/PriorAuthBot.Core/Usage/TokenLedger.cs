using System.Collections.Concurrent;

namespace PriorAuthBot.Core.Usage;

/// <summary>One user's usage today.</summary>
public sealed record UserUsage(string UserId, DateOnly Day, long InputTokens, long OutputTokens, int ModelCalls, decimal Cost)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>Everyone's usage today: the numbers behind the cost dashboard.</summary>
public sealed record DailyUsage(DateOnly Day, long InputTokens, long OutputTokens, int ModelCalls, decimal Cost, int Users);

/// <summary>
/// Where token usage is counted, per user per UTC day (chapter 13). The in-memory ledger is right
/// for one API instance. With more than one, every instance must charge the same ledger, or a user
/// gets one budget per instance: put it in Redis (an atomic INCRBY per user and day) in chapter 14.
/// </summary>
public interface ITokenLedger
{
    ValueTask<UserUsage> ForUserAsync(string userId, CancellationToken cancellationToken = default);

    ValueTask<DailyUsage> TodayAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a call's usage. Returns the spend alert thresholds this charge crossed, usually none.</summary>
    ValueTask<IReadOnlyList<double>> ChargeAsync(string userId, long inputTokens, long outputTokens, decimal cost, CancellationToken cancellationToken = default);

    /// <summary>When today's budgets reset: the next UTC midnight.</summary>
    DateTimeOffset ResetsAt { get; }
}

public sealed class InMemoryTokenLedger(Microsoft.Extensions.Options.IOptionsMonitor<UsageOptions> options, TimeProvider? time = null) : ITokenLedger
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(DateOnly Day, string User), Entry> _users = new();
    private readonly ConcurrentDictionary<DateOnly, Entry> _days = new();
    private readonly ConcurrentDictionary<(DateOnly Day, double Threshold), bool> _alerts = new();
    private int _prunedDay;

    public DateTimeOffset ResetsAt
    {
        get
        {
            var now = _time.GetUtcNow();
            return new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        }
    }

    private DateOnly Day => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    public ValueTask<UserUsage> ForUserAsync(string userId, CancellationToken cancellationToken = default) => ValueTask.FromResult(ForUser(userId));

    public ValueTask<DailyUsage> TodayAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Today());

    public ValueTask<IReadOnlyList<double>> ChargeAsync(string userId, long inputTokens, long outputTokens, decimal cost, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Charge(userId, inputTokens, outputTokens, cost));

    public UserUsage ForUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var day = Day;
        var entry = _users.GetValueOrDefault((day, userId)) ?? new Entry();
        lock (entry)
        {
            return new UserUsage(userId, day, entry.Input, entry.Output, entry.Calls, entry.Cost);
        }
    }

    public DailyUsage Today()
    {
        var day = Day;
        var entry = _days.GetValueOrDefault(day) ?? new Entry();
        lock (entry)
        {
            return new DailyUsage(day, entry.Input, entry.Output, entry.Calls, entry.Cost, _users.Keys.Count(k => k.Day == day));
        }
    }

    public IReadOnlyList<double> Charge(string userId, long inputTokens, long outputTokens, decimal cost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var day = Day;
        Add(_users.GetOrAdd((day, userId), _ => new Entry()), inputTokens, outputTokens, cost);
        var total = Add(_days.GetOrAdd(day, _ => new Entry()), inputTokens, outputTokens, cost);
        Prune(day);

        var settings = options.CurrentValue;
        if (settings.DailySpendAlertUsd <= 0)
        {
            return [];
        }

        // Each threshold alerts once per day, on the charge that crosses it, however many instances
        // of the dashboard are watching.
        return [.. settings.EffectiveThresholds
            .Where(t => total >= settings.DailySpendAlertUsd * (decimal)t && _alerts.TryAdd((day, t), true))];
    }

    private static decimal Add(Entry entry, long input, long output, decimal cost)
    {
        lock (entry)
        {
            entry.Input += input;
            entry.Output += output;
            entry.Calls++;
            entry.Cost += cost;
            return entry.Cost;
        }
    }

    private void Prune(DateOnly today)
    {
        // Once per day, by whichever charge sees the new day first.
        var previous = Volatile.Read(ref _prunedDay);
        if (previous == today.DayNumber || Interlocked.CompareExchange(ref _prunedDay, today.DayNumber, previous) != previous)
        {
            return;
        }

        foreach (var key in _users.Keys.Where(k => k.Day < today.AddDays(-1)))
        {
            _users.TryRemove(key, out _);
        }

        foreach (var key in _days.Keys.Where(k => k < today.AddDays(-31)))
        {
            _days.TryRemove(key, out _);
        }
    }

    private sealed class Entry
    {
        public long Input;
        public long Output;
        public int Calls;
        public decimal Cost;
    }
}
