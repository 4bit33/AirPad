using System.Collections.Concurrent;
using AirPad.Core.Protocol.Models;

namespace AirPad.Core.Protocol;

/// <summary>
/// Thread-safe registry of active AirPlay sessions, keyed by the
/// Active-Remote header value sent by the iPad.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public static SessionManager Current { get; } = new();

    public Task<Session> GetOrCreateAsync(string sessionId)
    {
        var session = _sessions.GetOrAdd(sessionId ?? "default", id => new Session(id));
        return Task.FromResult(session);
    }

    public Task<Session?> TryGetAsync(string? sessionId)
    {
        if (sessionId != null && _sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult<Session?>(session);
        return Task.FromResult<Session?>(null);
    }

    public Task SaveAsync(string sessionId, Session session)
    {
        _sessions[sessionId] = session;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<Session> All => _sessions.Values.ToArray();
}
