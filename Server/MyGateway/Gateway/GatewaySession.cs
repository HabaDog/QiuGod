using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

/// <summary>
/// Owns one client TCP connection and guarantees that its outbound packets are
/// written in order by a single sender loop.
/// </summary>
public sealed class GatewaySession : IAsyncDisposable
{
    private const int MaxQueuedOutboundPackets = 256;
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxQueuedOutboundPackets)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _sendLoop;

    public GatewaySession(TcpClient client)
    {
        Client = client;
        Stream = client.GetStream();
        SessionId = Guid.NewGuid();
        _sendLoop = Task.Run(SendLoopAsync);
    }

    public Guid SessionId { get; }
    public TcpClient Client { get; }
    public NetworkStream Stream { get; }
    public int? PlayerId { get; private set; }
    public string? SessionToken { get; private set; }
    public IPEndPoint? UdpEndPoint { get; private set; }

    public void BindPlayer(int playerId)
    {
        PlayerId = playerId;
    }

    public void BindAuthenticatedPlayer(int playerId, string sessionToken)
    {
        PlayerId = playerId;
        SessionToken = sessionToken;
    }

    public void BindUdpEndPoint(IPEndPoint endPoint)
    {
        UdpEndPoint = endPoint;
    }

    public void RevokePlayerBinding()
    {
        PlayerId = null;
        SessionToken = null;
        UdpEndPoint = null;
    }

    public bool TrySend(byte[] packet)
    {
        // A slow or disconnected client must not retain an unbounded backlog of
        // match snapshots in server memory. Callers treat a full queue as a failed
        // gateway send and detach the session through the existing error path.
        return !_cancellation.IsCancellationRequested && _outbound.Writer.TryWrite(packet);
    }

    public async ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();
        _cancellation.Cancel();

        try
        {
            await _sendLoop;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Stream.Close();
            Client.Close();
            _cancellation.Dispose();
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var packet in _outbound.Reader.ReadAllAsync(_cancellation.Token))
            {
                await Stream.WriteAsync(packet, _cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Gateway] Send failed for session {SessionId}: {exception.Message}");
        }
    }
}

public sealed class GatewaySessionRegistry
{
    private readonly ConcurrentDictionary<NetworkStream, GatewaySession> _sessions = new();
    private readonly ConcurrentDictionary<string, ResumableSession> _sessionsByToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _tokenByPlayerId = new();
    private readonly ConcurrentDictionary<string, GatewaySession> _sessionsByUdpEndPoint = new(StringComparer.Ordinal);
    private readonly object _resumeLock = new();
    private readonly TimeSpan _resumeGracePeriod;

    public GatewaySessionRegistry(TimeSpan? resumeGracePeriod = null)
    {
        _resumeGracePeriod = resumeGracePeriod ?? TimeSpan.FromMinutes(1);
    }

    public GatewaySession Register(TcpClient client)
    {
        var session = new GatewaySession(client);
        if (!_sessions.TryAdd(session.Stream, session))
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("The gateway could not register a client session.");
        }

        Console.WriteLine($"[Gateway] Session opened: {session.SessionId}, remote={client.Client.RemoteEndPoint}");
        return session;
    }

    public void BindPlayer(NetworkStream stream, int playerId)
    {
        if (_sessions.TryGetValue(stream, out var session))
        {
            session.BindPlayer(playerId);
        }
    }

    public string IssueSessionToken(NetworkStream stream, int playerId)
    {
        if (!_sessions.TryGetValue(stream, out var session))
        {
            throw new InvalidOperationException("Cannot issue a token for an unknown gateway session.");
        }

        lock (_resumeLock)
        {
            InvalidatePlayerUnsafe(playerId);

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            session.BindAuthenticatedPlayer(playerId, token);
            _sessionsByToken[token] = new ResumableSession(token, playerId, session);
            _tokenByPlayerId[playerId] = token;
            return token;
        }
    }

    public bool TryResumeSession(NetworkStream stream, string token, int playerId, out GatewaySession? session)
    {
        session = null;
        if (!_sessions.TryGetValue(stream, out var newSession) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (_resumeLock)
        {
            if (!_sessionsByToken.TryGetValue(token, out var resumable)
                || resumable.PlayerId != playerId
                || (resumable.ExpiresAtUtc.HasValue && resumable.ExpiresAtUtc.Value < DateTime.UtcNow))
            {
                RemoveTokenUnsafe(token);
                return false;
            }

            if (resumable.AttachedSession?.UdpEndPoint is not null)
            {
                _sessionsByUdpEndPoint.TryRemove(GetEndPointKey(resumable.AttachedSession.UdpEndPoint), out _);
            }

            if (resumable.AttachedSession is not null
                && !ReferenceEquals(resumable.AttachedSession, newSession))
            {
                resumable.AttachedSession.RevokePlayerBinding();
            }

            resumable.Attach(newSession);
            newSession.BindAuthenticatedPlayer(playerId, token);
            session = newSession;
            return true;
        }
    }

    /// <summary>
    /// Explicit logout is treated like a temporary disconnect. Detach the
    /// authenticated connection while keeping its resumable token alive until
    /// the normal grace-period cleanup expires.
    /// </summary>
    public bool DetachForLogout(NetworkStream stream, int playerId, out DateTime expiresAtUtc)
    {
        expiresAtUtc = DateTime.UtcNow + _resumeGracePeriod;
        if (!_sessions.TryGetValue(stream, out var session))
        {
            return false;
        }

        lock (_resumeLock)
        {
            if (!string.IsNullOrWhiteSpace(session.SessionToken)
                && _sessionsByToken.TryGetValue(session.SessionToken, out var resumable)
                && resumable.PlayerId == playerId
                && ReferenceEquals(resumable.AttachedSession, session))
            {
                resumable.Detach(expiresAtUtc);
                if (session.UdpEndPoint is not null)
                {
                    _sessionsByUdpEndPoint.TryRemove(GetEndPointKey(session.UdpEndPoint), out _);
                }

                session.RevokePlayerBinding();
                return true;
            }
        }

        return false;
    }

    public bool TryBindUdpEndPoint(string sessionToken, int playerId, IPEndPoint endPoint)
    {
        if (!_sessionsByToken.TryGetValue(sessionToken, out var resumable)
            || resumable.PlayerId != playerId
            || resumable.AttachedSession is not { } session)
        {
            return false;
        }

        if (session.UdpEndPoint is not null)
        {
            _sessionsByUdpEndPoint.TryRemove(GetEndPointKey(session.UdpEndPoint), out _);
        }

        session.BindUdpEndPoint(endPoint);
        _sessionsByUdpEndPoint[GetEndPointKey(endPoint)] = session;
        return true;
    }

    public bool IsUdpEndPointAuthorized(IPEndPoint endPoint)
    {
        return _sessionsByUdpEndPoint.ContainsKey(GetEndPointKey(endPoint));
    }

    public bool IsUdpPlayerAuthorized(IPEndPoint endPoint, int playerId)
    {
        return _sessionsByUdpEndPoint.TryGetValue(GetEndPointKey(endPoint), out var session)
            && session.PlayerId == playerId;
    }

    public bool TryGetSessionByUdpEndPoint(IPEndPoint endPoint, out GatewaySession? session)
    {
        return _sessionsByUdpEndPoint.TryGetValue(GetEndPointKey(endPoint), out session);
    }

    public bool TrySend(NetworkStream stream, byte[] packet)
    {
        return _sessions.TryGetValue(stream, out var session) && session.TrySend(packet);
    }

    public async ValueTask<int?> RemoveAsync(NetworkStream stream)
    {
        if (_sessions.TryRemove(stream, out var session))
        {
            int? detachedPlayerId = null;
            lock (_resumeLock)
            {
                if (!string.IsNullOrEmpty(session.SessionToken)
                    && _sessionsByToken.TryGetValue(session.SessionToken, out var resumable)
                    && ReferenceEquals(resumable.AttachedSession, session))
                {
                    resumable.Detach(DateTime.UtcNow + _resumeGracePeriod);
                    detachedPlayerId = resumable.PlayerId;
                }

                if (session.UdpEndPoint is not null)
                {
                    _sessionsByUdpEndPoint.TryRemove(GetEndPointKey(session.UdpEndPoint), out _);
                }
            }

            Console.WriteLine($"[Gateway] Session closed: {session.SessionId}");
            await session.DisposeAsync();
            return detachedPlayerId;
        }

        return null;
    }

    public bool IsPlayerConnected(int playerId)
    {
        return _tokenByPlayerId.TryGetValue(playerId, out var token)
            && _sessionsByToken.TryGetValue(token, out var resumable)
            && resumable.AttachedSession is not null;
    }

    public bool TryGetActiveSession(int playerId, out GatewaySession? session)
    {
        session = null;
        lock (_resumeLock)
        {
            if (!_tokenByPlayerId.TryGetValue(playerId, out var token)
                || !_sessionsByToken.TryGetValue(token, out var resumable)
                || resumable.AttachedSession is null)
            {
                return false;
            }

            session = resumable.AttachedSession;
            return true;
        }
    }

    public bool ExpireDisconnectedPlayer(int playerId)
    {
        lock (_resumeLock)
        {
            if (!_tokenByPlayerId.TryGetValue(playerId, out var token)
                || !_sessionsByToken.TryGetValue(token, out var resumable)
                || resumable.AttachedSession is not null
                || !resumable.ExpiresAtUtc.HasValue
                || resumable.ExpiresAtUtc.Value > DateTime.UtcNow)
            {
                return false;
            }

            RemoveTokenUnsafe(token);
            return true;
        }
    }

    public void InvalidatePlayer(int playerId)
    {
        lock (_resumeLock)
        {
            InvalidatePlayerUnsafe(playerId);
        }
    }

    public void DisposeAll()
    {
        var sessions = _sessions.ToArray();
        _sessions.Clear();
        _sessionsByToken.Clear();
        _tokenByPlayerId.Clear();
        _sessionsByUdpEndPoint.Clear();
        foreach (var session in sessions)
        {
            session.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string GetEndPointKey(IPEndPoint endPoint)
    {
        return $"{endPoint.Address}:{endPoint.Port}";
    }

    private void InvalidatePlayerUnsafe(int playerId)
    {
        if (_tokenByPlayerId.TryRemove(playerId, out var oldToken))
        {
            RemoveTokenUnsafe(oldToken);
        }
    }

    private void RemoveTokenUnsafe(string token)
    {
        if (!_sessionsByToken.TryRemove(token, out var resumable))
        {
            return;
        }

        if (_tokenByPlayerId.TryGetValue(resumable.PlayerId, out var currentToken)
            && string.Equals(currentToken, token, StringComparison.Ordinal))
        {
            _tokenByPlayerId.TryRemove(resumable.PlayerId, out _);
        }
        if (resumable.AttachedSession?.UdpEndPoint is not null)
        {
            _sessionsByUdpEndPoint.TryRemove(GetEndPointKey(resumable.AttachedSession.UdpEndPoint), out _);
        }
        resumable.AttachedSession?.RevokePlayerBinding();
    }

    private sealed class ResumableSession
    {
        public ResumableSession(string token, int playerId, GatewaySession attachedSession)
        {
            Token = token;
            PlayerId = playerId;
            AttachedSession = attachedSession;
        }

        public string Token { get; }
        public int PlayerId { get; }
        public GatewaySession? AttachedSession { get; private set; }
        public DateTime? ExpiresAtUtc { get; private set; }

        public void Attach(GatewaySession session)
        {
            AttachedSession = session;
            ExpiresAtUtc = null;
        }

        public void Detach(DateTime expiresAtUtc)
        {
            AttachedSession = null;
            ExpiresAtUtc = expiresAtUtc;
        }
    }
}
