using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IBFTSocketService.Services
{
    /// <summary>
    /// Unified logging service that consolidates all logs into a single file with 200MB rolling
    /// Ensures atomic request/response logging without interleaving
    /// </summary>
    public class UnifiedLogger : IDisposable
    {
        private readonly string _logDirectory;
        private readonly string _logFileBasePath;
        private readonly long _maxFileSizeBytes = 200 * 1024 * 1024; // 200MB
        private readonly SemaphoreSlim _semaphore;
        private readonly ILogger<UnifiedLogger> _logger;
        private string _currentLogFile;        
        private volatile bool _isDisposed = false;
        public UnifiedLogger(ILogger<UnifiedLogger> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            _logFileBasePath = Path.Combine(_logDirectory, "unified");
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

            _currentLogFile = GetCurrentLogFile();
        }

        /// <summary>
        /// Log a complete request/response transaction atomically
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
            catch (ObjectDisposedException)
            {
                // Semaphore đã bị dispose, bỏ qua
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging transaction | LogID: {LogId}", logId);
            }
        }

        /// <summary>
        /// Log a general message
        /// </summary>
        public async Task LogMessageAsync(string level, string message, string logId = null)
        {
            try
            {
                await _semaphore.WaitAsync();

                try
                {
                    string logEntry = FormatMessageLog(level, message, logId);
                    await AppendToFileAsync(logEntry);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging message");
            }
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

        private string FormatMessageLog(string level, string message, string logId)
        {
            var sb = new StringBuilder();
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logIdPart = string.IsNullOrEmpty(logId) ? "" : $" | LogID: {logId}";
            sb.AppendLine($"[{timestamp}] [{level:u3}]{logIdPart} {message}");
            return sb.ToString();
        }

        private string GetCurrentLogFile()
        {
            // Find the latest log file or create a new one
            int fileIndex = 1;
            string logFile = $"{_logFileBasePath}.log";

            // Check if we need to roll to a new file
            if (File.Exists(logFile))
            {
                var fileInfo = new FileInfo(logFile);
                if (fileInfo.Length >= _maxFileSizeBytes)
                {
                    // Find next available index
                    while (File.Exists($"{_logFileBasePath}.{fileIndex}.log"))
                    {
                        fileIndex++;
                    }
                    logFile = $"{_logFileBasePath}.{fileIndex}.log";
                }
            }

            return logFile;
        }

        private async Task AppendToFileAsync(string content)
        {
            if (string.IsNullOrEmpty(content) || _isDisposed)
                return;

            int retryCount = 0;
            const int maxRetries = 5;
            const int retryDelayMs = 200;

            while (retryCount < maxRetries)
            {
                try
                {
                    using (var fileStream = new FileStream(
                        _currentLogFile,
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
                    return;
                }
                catch (IOException) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    await Task.Delay(retryDelayMs * (retryCount + 1)); // Exponential backoff
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error writing log");
                    return; // Don't crash service for logging error
                }
            }
    }

        public void Dispose()
        {
            _isDisposed = true;
            _semaphore?.Dispose();
        }
    }
}
