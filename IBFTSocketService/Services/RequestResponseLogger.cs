using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IBFTSocketService.Services
{
    /// <summary>
    /// Dedicated service for logging request/response pairs to a file
    /// </summary>
    public class RequestResponseLogger
    {
        private readonly string _logDirectory;
        private readonly string _logFilePath;
        private readonly SemaphoreSlim _semaphore;
        private readonly ILogger<RequestResponseLogger> _logger;

        public RequestResponseLogger(ILogger<RequestResponseLogger> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs", "request-response");
            _logFilePath = Path.Combine(_logDirectory, $"request-response-{DateTime.Now:yyyy-MM-dd}.log");
            _semaphore = new SemaphoreSlim(1, 1);

            // Ensure log directory exists
            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating log directory: {LogDirectory}", _logDirectory);
            }
        }

        /// <summary>
        /// Log a request to file
        /// </summary>
        public async Task LogRequestAsync(string logId, string clientId, string requestContent, int requestSize)
        {
            try
            {
                await _semaphore.WaitAsync();

                try
                {
                    string logEntry = FormatRequestLog(logId, clientId, requestContent, requestSize);
                    await AppendToFileAsync(logEntry);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging request | LogID: {LogId}", logId);
            }
        }

        /// <summary>
        /// Log a response to file
        /// </summary>
        public async Task LogResponseAsync(string logId, string clientId, string responseContent, long responseTimeMs, bool success)
        {
            try
            {
                await _semaphore.WaitAsync();

                try
                {
                    string logEntry = FormatResponseLog(logId, clientId, responseContent, responseTimeMs, success);
                    await AppendToFileAsync(logEntry);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging response | LogID: {LogId}", logId);
            }
        }

        /// <summary>
        /// Log a complete request/response pair
        /// </summary>
        public async Task LogTransactionAsync(
            string logId,
            string clientId,
            string requestContent,
            int requestSize,
            string responseContent,
            long responseTimeMs,
            bool success)
        {
            try
            {
                await _semaphore.WaitAsync();

                try
                {
                    string logEntry = FormatTransactionLog(
                        logId, clientId, requestContent, requestSize, responseContent, responseTimeMs, success);
                    await AppendToFileAsync(logEntry);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging transaction | LogID: {LogId}", logId);
            }
        }

        private string FormatRequestLog(string logId, string clientId, string requestContent, int requestSize)
        {
            var sb = new StringBuilder();
            sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine($"[REQUEST] Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"LogID: {logId}");
            sb.AppendLine($"ClientID: {clientId}");
            sb.AppendLine($"Size: {requestSize} bytes");
            sb.AppendLine("────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine("Content:");
            sb.AppendLine(requestContent);
            sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            return sb.ToString();
        }

        private string FormatResponseLog(string logId, string clientId, string responseContent, long responseTimeMs, bool success)
        {
            var sb = new StringBuilder();
            sb.AppendLine("────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine($"[RESPONSE] Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"LogID: {logId}");
            sb.AppendLine($"ClientID: {clientId}");
            sb.AppendLine($"Status: {(success ? "SUCCESS" : "ERROR")}");
            sb.AppendLine($"Response Time: {responseTimeMs}ms");
            sb.AppendLine($"Size: {responseContent.Length} bytes");
            sb.AppendLine("────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine("Content:");
            sb.AppendLine(responseContent);
            sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            return sb.ToString();
        }

        private string FormatTransactionLog(
            string logId,
            string clientId,
            string requestContent,
            int requestSize,
            string responseContent,
            long responseTimeMs,
            bool success)
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔════════════════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine($"║ TRANSACTION LOG | Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"║ LogID: {logId}");
            sb.AppendLine($"║ ClientID: {clientId}");
            sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║ REQUEST");
            sb.AppendLine("╠────────────────────────────────────────────────────────────────────────────────╣");
            sb.AppendLine($"║ Size: {requestSize} bytes");
            sb.AppendLine("║");
            foreach (var line in requestContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                sb.AppendLine($"║ {line}");
            }
            sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║ RESPONSE");
            sb.AppendLine("╠────────────────────────────────────────────────────────────────────────────────╣");
            sb.AppendLine($"║ Status: {(success ? "SUCCESS" : "ERROR")}");
            sb.AppendLine($"║ Response Time: {responseTimeMs}ms");
            sb.AppendLine($"║ Size: {responseContent.Length} bytes");
            sb.AppendLine("║");
            foreach (var line in responseContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                sb.AppendLine($"║ {line}");
            }
            sb.AppendLine("╚════════════════════════════════════════════════════════════════════════════════╝");
            sb.AppendLine();

            return sb.ToString();
        }

        private async Task AppendToFileAsync(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            try
            {
                // Check if we need to create a new file for today
                string currentLogFile = Path.Combine(_logDirectory, $"request-response-{DateTime.Now:yyyy-MM-dd}.log");

                // Ensure directory exists before writing
                if (!Directory.Exists(_logDirectory))
                {
                    try
                    {
                        Directory.CreateDirectory(_logDirectory);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error creating log directory during write: {LogDirectory}", _logDirectory);
                        throw;
                    }
                }

                int retryCount = 0;
                const int maxRetries = 3;
                const int retryDelayMs = 100;

                while (retryCount < maxRetries)
                {
                    try
                    {
                        using (var fileStream = new FileStream(
                            currentLogFile,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.Read,
                            4096,
                            useAsync: true))
                        {
                            byte[] buffer = Encoding.UTF8.GetBytes(content);
                            await fileStream.WriteAsync(buffer, 0, buffer.Length);
                            await fileStream.FlushAsync();
                        }
                        return; // Success
                    }
                    catch (IOException) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        await Task.Delay(retryDelayMs);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing to request/response log file after retries");
                throw;
            }
        }
    }
}
