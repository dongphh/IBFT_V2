using IBFTSocketService.Core.Configuration;
using IBFTSocketService.Data.Repository;
using IBFTSocketService.Monitoring;
using IBFTSocketService.Services;
using SocketService;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net;

namespace IBFTSocketService
{
    /// <summary>
    /// IBFT version 2 Socket Service
    /// </summary>
    public class IBFT_V2_Service : BackgroundService
    {
        private readonly SocketServerConfig _config;
        private readonly IDataRepository _repository;
        private readonly ILogger<IBFT_V2_Service> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly PerformanceMonitor _monitor;
        private readonly OracleConnectionPoolManager _poolManager;
        private readonly RequestResponseLogger _requestResponseLogger;
        private readonly UnifiedLogger _unifiedLogger;
        private Socket _listeningSocket;
        private int _activeConnections;
        private readonly ConcurrentDictionary<string, SocketClientHandler> _clients;
        private Timer _statsTimer;
        private volatile bool _isShuttingDown;

        public IBFT_V2_Service(
            SocketServerConfig config,
            IDataRepository repository,
            ILogger<IBFT_V2_Service> logger,
            ILoggerFactory loggerFactory,
            PerformanceMonitor monitor,
            OracleConnectionPoolManager poolManager,
            RequestResponseLogger requestResponseLogger,
            UnifiedLogger unifiedLogger)
        {
            _config = config;
            _repository = repository;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _monitor = monitor;
            _poolManager = poolManager;
            _requestResponseLogger = requestResponseLogger;
            _unifiedLogger = unifiedLogger;
            _clients = new ConcurrentDictionary<string, SocketClientHandler>();
            _isShuttingDown = false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _listeningSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _listeningSocket.NoDelay = true;
                _listeningSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listeningSocket.Bind(new IPEndPoint(IPAddress.Any, _config.ListenPort));
                _listeningSocket.Listen(1000);

                _logger.LogInformation("════════════════════════════════════════════════════");
                _logger.LogInformation("🚀 Socket Server started successfully!");
                _logger.LogInformation("📡 Listening on port {Port}", _config.ListenPort);
                _logger.LogInformation("👥 Max Connections: {MaxConnections}", _config.MaxConnections);
                _logger.LogInformation("🏊 Connection Pools: 4 (Load Balanced)");
                _logger.LogInformation("════════════════════════════════════════════════════");

                // Start stats monitoring
                _statsTimer = new Timer(PrintStats, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

                // Accept clients
                while (!stoppingToken.IsCancellationRequested && !_isShuttingDown)
                {
                    try
                    {
                        var clientSocket = await _listeningSocket.AcceptAsync();

                        if (Interlocked.Increment(ref _activeConnections) > _config.MaxConnections)
                        {
                            Interlocked.Decrement(ref _activeConnections);
                            _logger.LogWarning("⚠️  Max connections reached, rejecting new connection");
                            clientSocket.Close();
                            continue;
                        }

                        _ = HandleClientConnectionAsync(clientSocket, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Socket was disposed during shutdown
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error accepting client connection");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Socket server error");
                throw;
            }
            finally
            {
                await GracefulShutdown();
            }
        }

        private async Task GracefulShutdown()
        {
            _isShuttingDown = true;

            _logger.LogWarning("🛑 Starting graceful shutdown...");
            _logger.LogInformation("Closing listening socket");

            // Stop accepting new connections
            try
            {
                _listeningSocket?.Close();
                _listeningSocket?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing listening socket");
            }

            // Stop stats timer
            try
            {
                _statsTimer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing stats timer");
            }

            // Gracefully close all client connections
            _logger.LogInformation("⏳ Closing {Count} active client connections", _clients.Count);

            var disconnectTasks = new List<Task>();

            foreach (var client in _clients.Values.ToList())
            {
                disconnectTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Give client time to finish current operation
                        await Task.Delay(500);
                        client?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing client handler");
                    }
                }));
            }

            // Wait for all clients to disconnect (max 10 seconds)
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(disconnectTasks),
                    Task.Delay(10000)
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for clients to disconnect");
            }

            // Clear clients dictionary
            _clients.Clear();

            // Clear connection pools
            _logger.LogInformation("Clearing Oracle connection pools");
            try
            {
                _poolManager?.ClearAllPools();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error clearing connection pools");
            }

            _logger.LogInformation("════════════════════════════════════════════════════");
            _logger.LogInformation("✅ Graceful shutdown complete");
            _logger.LogInformation("📊 Final Stats:");
            _logger.LogInformation("   - Active Connections Closed: {Count}", _clients.Count);
            _logger.LogInformation("════════════════════════════════════════════════════");
        }

        private async Task HandleClientConnectionAsync(Socket clientSocket, CancellationToken stoppingToken)
        {
            string clientId = clientSocket.RemoteEndPoint?.ToString() ?? "Unknown";
            SocketClientHandler handler = null;

            try
            {
                // ✅ Create logger using ILoggerFactory
                var clientLogger = _loggerFactory.CreateLogger<SocketClientHandler>();

                handler = new SocketClientHandler(clientSocket, _repository, clientLogger, _config, _requestResponseLogger, _unifiedLogger);

                // Use try-catch to prevent exceptions in event handler from crashing
                EventHandler<ClientDisconnectEventArgs> disconnectHandler = (s, e) =>
                {
                    try
                    {
                        _clients.TryRemove(clientId, out _);
                        Interlocked.Decrement(ref _activeConnections);
                        _monitor.RecordClientDisconnect();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in disconnect handler for {ClientId}", clientId);
                    }
                };

                handler.OnDisconnect += disconnectHandler;

                _clients.TryAdd(clientId, handler);
                _monitor.RecordClientConnect();

                await handler.HandleClientAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling client {ClientId}", clientId);
            }
            finally
            {
                // Ensure proper cleanup
                try
                {
                    handler?.Dispose();
                    _clients.TryRemove(clientId, out _);
                    Interlocked.Decrement(ref _activeConnections);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error in cleanup for {ClientId}", clientId);
                }
            }
        }

        private void PrintStats(object state)
        {
            try
            {
                var stats = _monitor.GetCurrentStats();
                _logger.LogInformation(
                    "📊 [STATS] Active: {Active} | Queries: {Queries} | Errors: {Errors} | Avg: {Avg}ms | Uptime: {Uptime}s",
                    _activeConnections < 0 ? 0 : _activeConnections,
                    stats.TotalQueries,
                    stats.ErrorCount,
                    stats.AverageResponseTime,
                    stats.UptimeSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error printing stats");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("🛑 StopAsync called - initiating graceful shutdown");
            await base.StopAsync(cancellationToken);
        }
    }
}
