namespace IBFTSocketService.Core.Configuration
{
    public class SocketServerConfig
    {
        public int ListenPort { get; set; } = 5000;
        public int MaxConnections { get; set; } = 5000;
        public int ReceiveBufferSize { get; set; } = 8192;
        public int SendBufferSize { get; set; } = 8192;
        public int ConnectionTimeout { get; set; } = 30000; // 30s
        public int IdleTimeout { get; set; } = 300000; // 5 min
        public int MaxRequestSize { get; set; } = 1048576; // 1MB

    }
}
