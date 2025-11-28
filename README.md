application.json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "System": "Warning",
      "Microsoft": "Warning",
      "Oracle.ManagedDataAccess": "Warning"
    }
  },
  "ConnectionStrings": {
    "OracleConnection": "..."
  },
  "SocketServer": {
    "ListenPort": 5000,
    "MaxConnections": 1000,
    "ReceiveBufferSize": 32768,
    "SendBufferSize": 32768,
    "ConnectionTimeout": 60000,
    "IdleTimeout": 900000,
    "MaxRequestSize": 1048576,
    "PartnerHost": "10.68.20.120",
    "PartnerPort": 14130
  },
  "OraclePool": {
    "MaxPoolSize": 100,
    "MinPoolSize": 20,  
    "IncrPoolSize": 5,
    "DecrPoolSize": 2,
    "ConnectionLifetime": 600,
    "ConnectionIdleTimeout": 300,
    "NumberOfPools": 4,
    "ValidateConnection": true,
    "CommandTimeout": 30
  }
}
