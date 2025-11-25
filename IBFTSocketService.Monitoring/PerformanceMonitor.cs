using System.Diagnostics;
using IBFTSocketService.Core.Configuration;
using IBFTSocketService.Core.Models;
using IBFTSocketService.Data.Repository;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace IBFTSocketService.Monitoring
{
    /// <summary>
    /// Performance monitoring service
    /// Tracks metrics and health of the socket service
    /// </summary>
    public class PerformanceMonitor
    {
        private int _totalConnections;
        private int _totalQueries;
        private int _errorCount;
        private int _clientDisconnects;
        private long _totalResponseTimeMs;
        private readonly Stopwatch _uptime;

        public PerformanceMonitor()
        {
            _uptime = Stopwatch.StartNew();
        }

        public void RecordClientConnect()
        {
            Interlocked.Increment(ref _totalConnections);
        }

        public void RecordClientDisconnect()
        {
            Interlocked.Increment(ref _clientDisconnects);
        }

        public void RecordQuery()
        {
            Interlocked.Increment(ref _totalQueries);
        }

        public void RecordError()
        {
            Interlocked.Increment(ref _errorCount);
        }

        public void RecordResponseTime(long responseTimeMs)
        {
            Interlocked.Add(ref _totalResponseTimeMs, responseTimeMs);
        }

        public PoolMetrics GetCurrentStats()
        {
            return new PoolMetrics
            {
                TotalConnections = _totalConnections,
                TotalQueries = _totalQueries,
                ErrorCount = _errorCount,
                ClientDisconnects = _clientDisconnects,
                UptimeSeconds = (long)_uptime.Elapsed.TotalSeconds,
                AverageResponseTime = _totalQueries > 0 ? _totalResponseTimeMs / _totalQueries : 0
            };
        }

        public async Task CheckDatabaseHealthAsync()
        {
            try
            {
                using (var conn = OracleConnectionPoolManager.Instance.GetConnection())
                {
                    await conn.OpenAsync();
                    using (var cmd = new OracleCommand("SELECT 1 FROM DUAL", conn))
                    {
                        cmd.CommandTimeout = 10;
                        await cmd.ExecuteScalarAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                RecordError();
            }
        }
    }

    /// <summary>
    /// Pool metrics data model
    /// </summary>
    public class PoolMetrics
    {
        public int TotalConnections { get; set; }
        public int TotalQueries { get; set; }
        public int ErrorCount { get; set; }
        public int ClientDisconnects { get; set; }
        public long UptimeSeconds { get; set; }
        public long AverageResponseTime { get; set; }

        public override string ToString()
        {
            return $"Connections: {TotalConnections}, Queries: {TotalQueries}, " +
                   $"Errors: {ErrorCount}, Uptime: {UptimeSeconds}s, " +
                   $"AvgResponse: {AverageResponseTime}ms";
        }
    }

    /// <summary>
    /// Health check service
    /// Monitors database and pool health
    /// </summary>
    public class HealthCheckService
    {
        private readonly ILogger<HealthCheckService> _logger;
        private readonly OraclePoolConfig _poolConfig;

        public HealthCheckService(ILogger<HealthCheckService> logger, OraclePoolConfig poolConfig)
        {
            _logger = logger;
            _poolConfig = poolConfig;
        }

        public async Task<HealthCheckResult> CheckAsync()
        {
            var result = new HealthCheckResult
            {
                IsHealthy = true,
                Status = "Healthy",
                CheckTime = DateTime.UtcNow
            };

            try
            {
                // Check database connection
                await CheckDatabaseConnectionAsync(result);

                // Check memory
                await CheckMemoryAsync(result);

                // Check pool status
                CheckPoolStatus(result);

                if (result.Details.ContainsKey("db_error") || result.Details.ContainsKey("memory_error"))
                {
                    result.IsHealthy = false;
                    result.Status = "Degraded";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                result.IsHealthy = false;
                result.Status = "Unhealthy";
                result.Details["error"] = ex.Message;
            }

            return result;
        }

        private async Task CheckDatabaseConnectionAsync(HealthCheckResult result)
        {
            try
            {
                using (var conn = OracleConnectionPoolManager.Instance.GetConnection())
                {
                    var sw = Stopwatch.StartNew();
                    await conn.OpenAsync();

                    using (var cmd = new OracleCommand("SELECT 1 FROM DUAL", conn))
                    {
                        cmd.CommandTimeout = 10;
                        await cmd.ExecuteScalarAsync();
                    }

                    sw.Stop();
                    result.Details["db_connection_time_ms"] = sw.ElapsedMilliseconds;
                    result.Details["db_status"] = "OK";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database connection check failed");
                result.Details["db_error"] = ex.Message;
                result.Details["db_status"] = "ERROR";
            }
        }

        private async Task CheckMemoryAsync(HealthCheckResult result)
        {
            try
            {
                var query = @"
                    SELECT 
                        COUNT(*) as session_count,
                        ROUND(SUM(VALUE)/1024/1024, 2) as pga_memory_mb
                    FROM v$sesstat
                    WHERE SID IN (SELECT SID FROM v$session WHERE USERNAME IS NOT NULL)";

                using (var conn = OracleConnectionPoolManager.Instance.GetConnection())
                {
                    await conn.OpenAsync();

                    using (var cmd = new OracleCommand(query, conn))
                    {
                        cmd.CommandTimeout = 10;

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var sessionCount = reader.GetDecimal(0);
                                var pgaMemory = reader.GetDecimal(1);

                                result.Details["session_count"] = sessionCount;
                                result.Details["pga_memory_mb"] = pgaMemory;

                                if (pgaMemory > 10000) // 10GB warning
                                {
                                    result.Details["memory_warning"] = "PGA memory > 10GB";
                                    result.Details["memory_error"] = "High memory usage detected";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory check failed");
                result.Details["memory_error"] = ex.Message;
            }
        }

        private void CheckPoolStatus(HealthCheckResult result)
        {
            try
            {
                var poolStats = OracleConnectionPoolManager.Instance.GetPoolStatistics();
                result.Details["pool_count"] = poolStats.Count;
                result.Details["pool_max_size"] = poolStats.Values.FirstOrDefault();
                result.Details["pool_status"] = "OK";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pool status check failed");
                result.Details["pool_error"] = ex.Message;
                result.Details["pool_status"] = "ERROR";
            }
        }
    }

    /// <summary>
    /// Real-time performance monitor
    /// Used for debugging and performance analysis
    /// </summary>
    public class RealtimePerformanceMonitor
    {
        private readonly ILogger<RealtimePerformanceMonitor> _logger;
        private readonly OraclePoolConfig _poolConfig;

        public RealtimePerformanceMonitor(ILogger<RealtimePerformanceMonitor> logger, OraclePoolConfig poolConfig)
        {
            _logger = logger;
            _poolConfig = poolConfig;
        }

        public async Task PrintDatabaseHealthAsync()
        {
            try
            {
                using (var conn = OracleConnectionPoolManager.Instance.GetConnection())
                {
                    await conn.OpenAsync();

                    var query = @"
                        SELECT 
                            COUNT(*) as active_sessions
                            --ROUND(SUM(CASE WHEN NAME='session pga memory' THEN VALUE ELSE 0 END)/1024/1024/1024, 2) as pga_gb,
                            --ROUND(AVG(CASE WHEN NAME='session pga memory' THEN VALUE ELSE 0 END)/1024/1024, 2) as avg_memory_mb
                        FROM v$sesstat
                        WHERE SID IN (SELECT SID FROM v$session WHERE USERNAME IS NOT NULL)";

                    using (var cmd = new OracleCommand(query, conn))
                    {
                        cmd.CommandTimeout = 30;

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                _logger.LogInformation("Database Health - Sessions: {Sessions}, PGA: {PGA}GB, Avg Memory: {AvgMemory}MB",
                                    reader.GetDecimal(0),
                                    reader.GetDecimal(1),
                                    reader.GetDecimal(2));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking database health");
            }
        }

        public async Task PrintTopMemorySessions(int limit = 10)
        {
            try
            {
                using (var conn = OracleConnectionPoolManager.Instance.GetConnection())
                {
                    await conn.OpenAsync();

                    var query = $@"
                        SELECT 
                            s.SID,
                            s.USERNAME,
                            SUBSTR(s.PROGRAM, 1, 30) as PROGRAM,
                            ROUND(st.VALUE/1024/1024, 2) as MEMORY_MB
                        FROM v$session s
                        JOIN v$sesstat st ON s.SID = st.SID
                        --WHERE st.NAME = 'session pga memory'
                        ORDER BY st.VALUE DESC
                        FETCH FIRST {limit} ROWS ONLY";

                    using (var cmd = new OracleCommand(query, conn))
                    {
                        cmd.CommandTimeout = 30;

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            _logger.LogInformation("=== Top {Limit} Memory Consuming Sessions ===", limit);

                            while (await reader.ReadAsync())
                            {
                                _logger.LogInformation("SID: {SID}, User: {User}, Program: {Program}, Memory: {Memory}MB",
                                    reader.GetDecimal(0),
                                    reader.GetString(1),
                                    reader.GetString(2),
                                    reader.GetDecimal(3));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting top memory sessions");
            }
        }
    }
}