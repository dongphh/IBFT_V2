using IBFTSocketService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;


namespace IBFTSocketService.Data.Repository
{
    public class DataRepository : IDataRepository
    {
        private readonly ILogger<DataRepository> _logger;
        private readonly OraclePoolConfig _poolConfig;
        public DataRepository(ILogger<DataRepository> logger, OraclePoolConfig poolConfig)
        {
            _logger = logger;
            _poolConfig = poolConfig;
        }
        public async Task<T> ExecuteScalarAsync<T>(string sql, Dictionary<string, object> parameters = null)
        {
            using (var connection = OracleConnectionPoolManager.Instance.GetConnection())
            {
                try
                {
                    await connection.OpenAsync();

                    using (var cmd = new OracleCommand(sql, connection))
                    {
                        cmd.CommandTimeout = _poolConfig.CommandTimeout;

                        if (parameters != null)
                        {
                            AddParameters(cmd, parameters);
                        }

                        var result = await cmd.ExecuteScalarAsync();
                        return result == null || result == DBNull.Value
                            ? default
                            : (T)Convert.ChangeType(result, typeof(T));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExecuteScalarAsync error: {SQL}", sql);
                    throw;
                }
                finally
                {
                    connection.Close();
                }
            }
        }

        public async Task<List<T>> ExecuteQueryAsync<T>(string sql, Dictionary<string, object> parameters = null,
            Func<OracleDataReader, T> mapFunction = null)
        {
            var results = new List<T>();

            using (var connection = OracleConnectionPoolManager.Instance.GetConnection())
            {
                try
                {
                    await connection.OpenAsync();

                    using (var cmd = new OracleCommand(sql, connection))
                    {
                        cmd.CommandTimeout = _poolConfig.CommandTimeout;

                        if (parameters != null)
                        {
                            AddParameters(cmd, parameters);
                        }

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                if (mapFunction != null)
                                {
                                    results.Add(mapFunction(reader));
                                }
                                else if (typeof(T) == typeof(string))
                                {
                                    results.Add((T)(object)reader.GetString(0));
                                }
                                else if (typeof(T) == typeof(int))
                                {
                                    results.Add((T)(object)reader.GetInt32(0));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExecuteQueryAsync error: {SQL}", sql);
                    throw;
                }
                finally
                {
                    connection.Close();
                }
            }

            return results;
        }

        public async Task<int> ExecuteNonQueryAsync(string sql, Dictionary<string, object> parameters = null)
        {
            using (var connection = OracleConnectionPoolManager.Instance.GetConnection())
            {
                try
                {
                    await connection.OpenAsync();

                    using (var cmd = new OracleCommand(sql, connection))
                    {
                        cmd.CommandTimeout = _poolConfig.CommandTimeout;

                        if (parameters != null)
                        {
                            AddParameters(cmd, parameters);
                        }

                        return await cmd.ExecuteNonQueryAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExecuteNonQueryAsync error: {SQL}", sql);
                    throw;
                }
                finally
                {
                    connection.Close();
                }
            }
        }
        private void AddParameters(OracleCommand cmd, Dictionary<string, object> parameters)
        {
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param.Key, param.Value ?? DBNull.Value);
            }
        }
    }
}
