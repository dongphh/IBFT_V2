using Oracle.ManagedDataAccess.Client;


namespace IBFTSocketService.Data.Repository
{

    /// <summary>
    /// Interface for data repository operations
    /// </summary>
    public interface IDataRepository
    {
        Task<T> ExecuteScalarAsync<T>(string sql, Dictionary<string, object> parameters = null);
        Task<List<T>> ExecuteQueryAsync<T>(string sql, Dictionary<string, object> parameters = null,
            Func<OracleDataReader, T>? mapFunction = null);
        Task<int> ExecuteNonQueryAsync(string sql, Dictionary<string, object> parameters = null);
    }
}
