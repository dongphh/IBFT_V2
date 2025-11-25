using IBFTSocketService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace IBFTSocketService.Data.Repository
{

    public class OracleConnectionPoolManager
    {
        private static readonly Lazy<OracleConnectionPoolManager> _instance
            = new Lazy<OracleConnectionPoolManager>(() => new OracleConnectionPoolManager());

        public static OracleConnectionPoolManager Instance => _instance.Value;

        private readonly Dictionary<string, PoolConfiguration> _pools;
        private ILogger<OracleConnectionPoolManager> _logger;
        private OraclePoolConfig _poolConfig;
        private Timer _monitoringTimer;
        private readonly object _lockObj = new object();
        private int _poolIndex = 0;
        public class PoolConfiguration
        {
            public string Name { get; set; }
            public string ConnectionString { get; set; }
            public int MaxPoolSize { get; set; }
            public int MinPoolSize { get; set; }
        }
        private OracleConnectionPoolManager()
        {

            _pools = new Dictionary<string, PoolConfiguration>();
            _poolConfig = new OraclePoolConfig();
        }

        public void Initialize(string baseConnectionString, OraclePoolConfig poolConfig,
             ILogger<OracleConnectionPoolManager> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _poolConfig = poolConfig ?? throw new ArgumentNullException(nameof(poolConfig));

            if (string.IsNullOrWhiteSpace(baseConnectionString))
            {
                throw new ArgumentException("Base connection string cannot be null or empty", nameof(baseConnectionString));
            }

            _logger.LogInformation("═══════════════════════════════════════════════════════");
            _logger.LogInformation("🔧 Initializing Oracle Connection Pool Manager");
            _logger.LogInformation("   Number of Pools: {PoolCount}", poolConfig.NumberOfPools);
            _logger.LogInformation("   Max Pool Size: {MaxSize}", poolConfig.MaxPoolSize);
            _logger.LogInformation("   Min Pool Size: {MinSize}", poolConfig.MinPoolSize);
            _logger.LogInformation("═══════════════════════════════════════════════════════");

            lock (_lockObj)
            {
                if (_pools.Count > 0)
                {
                    _logger.LogWarning("⚠️  Connection pools already initialized, skipping");
                    return;
                }

                for (int i = 1; i <= poolConfig.NumberOfPools; i++)
                {
                    string poolName = $"OraclePool_{i}";

                    try
                    {
                        var connStringBuilder = new OracleConnectionStringBuilder(baseConnectionString)
                        {
                            Pooling = true,
                            MaxPoolSize = poolConfig.MaxPoolSize,
                            MinPoolSize = poolConfig.MinPoolSize,
                            IncrPoolSize = poolConfig.IncrPoolSize,
                            DecrPoolSize = poolConfig.DecrPoolSize,
                            ConnectionLifeTime = poolConfig.ConnectionLifetime,
                            ValidateConnection = poolConfig.ValidateConnection
                        };

                        var pool = new PoolConfiguration
                        {
                            Name = poolName,
                            ConnectionString = connStringBuilder.ConnectionString,
                            MaxPoolSize = poolConfig.MaxPoolSize,
                            MinPoolSize = poolConfig.MinPoolSize
                        };

                        _pools.Add(poolName, pool);
                        _logger.LogInformation("   ✓ Pool '{PoolName}' created - Max: {Max}, Min: {Min}",
                            poolName, poolConfig.MaxPoolSize, poolConfig.MinPoolSize);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Failed to create pool {PoolName}", poolName);
                        throw;
                    }
                }

                _logger.LogInformation("✅ Oracle Connection Pool Manager initialized successfully!");
                _logger.LogInformation("   Total Pools: {Count}, Total Connections: {TotalConnections}",
                    _pools.Count, _pools.Count * poolConfig.MaxPoolSize);
            }

            // Start monitoring
            _monitoringTimer = new Timer(MonitorPoolHealth, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public OracleConnection GetConnection(string poolName = null)
        {
            if (_pools == null || _pools.Count == 0)
            {
                throw new InvalidOperationException(
                    "❌ No connection pools available. " +
                    "Ensure OracleConnectionPoolManager.Initialize() is called during startup. " +
                    "Check that appsettings.json has ConnectionStrings:OracleConnection configured.");
            }

            if (string.IsNullOrEmpty(poolName))
            {
                poolName = SelectOptimalPool();
            }

            lock (_lockObj)
            {
                if (_pools.TryGetValue(poolName, out var poolConfig))
                {
                    try
                    {
                        var connection = new OracleConnection(poolConfig.ConnectionString);
                        return connection;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError("❌ Error getting connection from pool {PoolName}: {Error}",
                            poolName, ex.Message);
                        throw;
                    }
                }
            }

            throw new InvalidOperationException($"❌ Pool {poolName} not found");
        }
        private string SelectOptimalPool()
        {
            var poolNames = _pools.Keys.ToList();
            if (poolNames.Count == 0)
                throw new InvalidOperationException("No connection pools available");

            // Round-robin load balancing
            var selectedPool = poolNames[_poolIndex % poolNames.Count];
            Interlocked.Increment(ref _poolIndex);

            return selectedPool;
        }

        private void MonitorPoolHealth(object state)
        {
            try
            {
                foreach (var pool in _pools.Values)
                {
                    try
                    {
                        using (var testConn = new OracleConnection(pool.ConnectionString))
                        {
                            testConn.Open();
                            using (var cmd = new OracleCommand("SELECT 1 FROM DUAL", testConn))
                            {
                                cmd.CommandTimeout = 10;
                                cmd.ExecuteScalar();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("Pool {PoolName} health check failed: {Error}",
                            pool.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error monitoring pool health");
            }
        }

        public void ClearAllPools()
        {
            try
            {
                OracleConnection.ClearAllPools();
                _logger?.LogInformation("All connection pools cleared");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error clearing connection pools");
            }
        }

        public Dictionary<string, int> GetPoolStatistics()
        {
            return _pools.ToDictionary(p => p.Key, p => p.Value.MaxPoolSize);
        }
    }
}
