
namespace IBFTSocketService.Core.Models
{
    public class ServerResponse
    {
        public bool Success { get; set; }
        public required string Message { get; set; }
        public object? Data { get; set; }
        public required string ErrorCode { get; set; }
        public long ExecutionTimeMs { get; set; }
        public string? LogId { get; set; } // Add LogId
        public override string ToString()
        {
            return $"LOGID:{LogId}|SUCCESS:{Success}|MESSAGE:{Message}|DATA:{Data}|ERROR:{ErrorCode}|TIME:{ExecutionTimeMs}ms";
        }

        public static ServerResponse Ok(object? data = null, long executionTimeMs = 0,string? logId = null)
        {
            return new ServerResponse
            {
                Success = true,
                Message = "Success",
                Data = data,
                ExecutionTimeMs = executionTimeMs,
                ErrorCode = "200",
                LogId = logId
            };
        }

        public static ServerResponse Error(string message, string errorCode = "ERR_GENERAL", long executionTimeMs = 0,string? logId=null)
        {
            return new ServerResponse
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
                ExecutionTimeMs = executionTimeMs,
                LogId = logId
            };
        }
    }
}
