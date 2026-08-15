using System.Collections.Concurrent;

namespace RelayForge.Panel.Api;

public sealed class LoginRateLimiter
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _cleanupLock = new();
    private long _nextCleanupTicks = DateTime.MinValue.Ticks;

    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        var now = DateTimeOffset.UtcNow;
        CleanupExpired(now);
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            if (entry.LockedUntil > now)
            {
                retryAfter = entry.LockedUntil - now;
                return false;
            }

            if (now - entry.WindowStarted > Window)
            {
                entry.WindowStarted = now;
                entry.Failures = 0;
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void RegisterFailure(string key)
    {
        var now = DateTimeOffset.UtcNow;
        CleanupExpired(now);
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            if (now - entry.WindowStarted > Window)
            {
                entry.WindowStarted = now;
                entry.Failures = 0;
            }

            entry.Failures++;
            if (entry.Failures >= MaxFailures) entry.LockedUntil = now.Add(Lockout);
        }
    }

    public void RegisterSuccess(string key) => _entries.TryRemove(key, out _);

    private void CleanupExpired(DateTimeOffset now)
    {
        if (now.UtcDateTime.Ticks < Volatile.Read(ref _nextCleanupTicks)) return;
        lock (_cleanupLock)
        {
            if (now.UtcDateTime.Ticks < Volatile.Read(ref _nextCleanupTicks)) return;
            Volatile.Write(ref _nextCleanupTicks, now.AddMinutes(1).UtcDateTime.Ticks);
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                lock (entry)
                {
                    if (entry.LockedUntil <= now && now - entry.WindowStarted > Window)
                        ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(pair);
                }
            }
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset WindowStarted { get; set; } = DateTimeOffset.UtcNow;
        public int Failures { get; set; }
        public DateTimeOffset LockedUntil { get; set; }
    }
}
