﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace SapNwRfc.Pooling
{
    public sealed class SapConnectionPool : ISapConnectionPool
    {
        private readonly SapConnectionParameters _connectionParameters;
        private readonly int _poolSize;
        private readonly Func<SapConnectionParameters, ISapConnection> _connectionFactory;
        private readonly TimeSpan _connectionIdleTimeout;
        private readonly object _syncRoot = new object();
        private readonly ConcurrentQueue<(ISapConnection Connection, DateTime ExpiresAtUtc)> _idleConnections = new ConcurrentQueue<(ISapConnection Connection, DateTime ExpiresAtUtc)>();
        private readonly SemaphoreSlim _idleConnectionSemaphore = new SemaphoreSlim(0);
        private readonly Timer _timer;

        private int _openConnectionCount = 0;

        [ExcludeFromCodeCoverage]
        [SuppressMessage("ReSharper", "RedundantOverload.Global", Justification = "Public constructor should not expose connection factory")]
        internal SapConnectionPool(
            string connectionString,
            int poolSize = 5,
            TimeSpan? connectionIdleTimeout = null,
            TimeSpan? idleDetectionInterval = null)
            : this(connectionString, poolSize, connectionIdleTimeout, idleDetectionInterval, null)
        {
        }

        internal SapConnectionPool(
            string connectionString,
            int poolSize = 5,
            TimeSpan? connectionIdleTimeout = null,
            TimeSpan? idleDetectionInterval = null,
            Func<SapConnectionParameters, ISapConnection> connectionFactory = null)
        {
            _connectionParameters = SapConnectionParameters.Parse(connectionString);
            _poolSize = poolSize;
            _connectionIdleTimeout = connectionIdleTimeout ?? TimeSpan.FromSeconds(30);
            _connectionFactory = connectionFactory ?? (parameters => new SapConnection(parameters));
            _timer = new Timer(
                callback: _ => DisposeIdleConnections(),
                state: null,
                dueTime: idleDetectionInterval ?? TimeSpan.FromSeconds(1),
                period: idleDetectionInterval ?? TimeSpan.FromSeconds(1));
        }

        public void Dispose()
        {
            _timer.Dispose();
            while (_idleConnections.TryDequeue(out (ISapConnection Connection, DateTime ExpiresAtUtc) idleConnection))
                idleConnection.Connection.Dispose();
        }

        [SuppressMessage("ReSharper", "InvertIf", Justification = "Don't change double-checked lock")]
        public ISapConnection GetConnection(CancellationToken cancellationToken = default)
        {
            // See if there is an idling connection available, but don't wait for it
            if (_idleConnectionSemaphore.Wait(TimeSpan.Zero, cancellationToken))
            {
                lock (_syncRoot)
                    if (_idleConnections.TryDequeue(out (ISapConnection Connection, DateTime ExpiresAtUtc) idleConnection))
                        return idleConnection.Connection;
            }

            while (true)
            {
                if (_openConnectionCount < _poolSize)
                {
                    ISapConnection connection = null;

                    lock (_syncRoot)
                    {
                        if (_openConnectionCount < _poolSize)
                        {
                            _openConnectionCount++;
                            connection = _connectionFactory(_connectionParameters);
                        }
                    }

                    if (connection != null)
                    {
                        connection.Connect();
                        return connection;
                    }
                }

                _idleConnectionSemaphore.Wait(cancellationToken);

                lock (_syncRoot)
                    if (_idleConnections.TryDequeue(out (ISapConnection Connection, DateTime ExpiresAtUtc) idleConnection))
                        return idleConnection.Connection;
            }
        }

        public void ReturnConnection(ISapConnection connection)
        {
            DateTime expiresAtUtc = DateTime.UtcNow + _connectionIdleTimeout;
            _idleConnections.Enqueue((Connection: connection, ExpiresAtUtc: expiresAtUtc));
            _idleConnectionSemaphore.Release();
        }

        public void ForgetConnection(ISapConnection connection)
        {
            connection.Dispose();
            lock (_syncRoot) _openConnectionCount--;
            _idleConnectionSemaphore.Release();
        }

        [SuppressMessage("ReSharper", "InvertIf", Justification = "Don't change double-checked lock")]
        private void DisposeIdleConnections()
        {
            while (true)
            {
                // The first connection in the queue is the one that has been idling the longest.
                // So, when the first connection did not expire, the rest didn't expire too.
                if (!_idleConnections.TryPeek(out (ISapConnection Connection, DateTime ExpiresAtUtc) idleConnection) ||
                    idleConnection.ExpiresAtUtc > DateTime.UtcNow)
                    return;

                lock (_syncRoot)
                {
                    if (!_idleConnections.TryPeek(out idleConnection) || idleConnection.ExpiresAtUtc > DateTime.UtcNow)
                        return;

                    // Remove idling connection from queue
                    _idleConnections.TryDequeue(out _);

                    // Decrease semaphore count as we removed an idling connection
                    _idleConnectionSemaphore.Wait();

                    // Dispose the idling connection
                    idleConnection.Connection.Dispose();

                    Debug.Assert(_openConnectionCount > 0, "Open connection count must be greater than 0");
                    _openConnectionCount--;
                }
            }
        }
    }
}
