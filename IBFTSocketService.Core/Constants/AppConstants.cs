namespace IBFTSocketService.Core.Constants
{
    public class AppConstants
    {
        public const string COMMAND_QUERY = "QUERY";
        public const string COMMAND_EXEC = "EXEC";
        public const string COMMAND_HEALTH = "HEALTH";
        public const string COMMAND_STATS = "STATS";

        public const string ERROR_INVALID_COMMAND = "ERR_INVALID_CMD";
        public const string ERROR_DATABASE = "ERR_DB";
        public const string ERROR_TIMEOUT = "ERR_TIMEOUT";
        public const string ERROR_POOL_EXHAUSTED = "ERR_POOL_FULL";

        public const int MAX_MESSAGE_SIZE = 1048576; // 1MB
        public const int HEARTBEAT_INTERVAL = 60000; // 60s
    }
}
