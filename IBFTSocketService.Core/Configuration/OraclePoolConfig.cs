namespace IBFTSocketService.Core.Configuration
{
    public class OraclePoolConfig
    {
        public int MaxPoolSize { get; set; } = 100;
        public int MinPoolSize { get; set; } = 20;
        public int IncrPoolSize { get; set; } = 5;
        public int DecrPoolSize { get; set; } = 2;
        public int ConnectionLifetime { get; set; } = 600; // 10 min
        public int ConnectionIdleTimeout { get; set; } = 300; // 5 min
        public int NumberOfPools { get; set; } = 4;
        public bool ValidateConnection { get; set; } = true;
        public int CommandTimeout { get; set; } = 30;
    }
}
