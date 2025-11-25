using System.Text;

namespace IBFTSocketService.Services
{
    /// <summary>
    /// Unified Logger với Rolling File (200MB per file)
    /// Ghi tất cả log vào 1 file duy nhất với Log ID
    /// </summary>
    public class UnifiedLogger : IDisposable
    {
        private readonly string _logDirectory;
        private readonly long _maxFileSizeBytes = 200 * 1024 * 1024; // 200MB
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private string _currentLogFilePath;
        private long _currentFileSize;
        private bool _disposed;

        public UnifiedLogger(string logDirectory = null)
        {
            _logDirectory = logDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

            // Tạo thư mục log nếu chưa tồn tại
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            // Khởi tạo file log hiện tại
            InitializeCurrentLogFile();
        }

        /// <summary>
        /// Khởi tạo hoặc lấy file log hiện tại
        /// </summary>
        private void InitializeCurrentLogFile()
        {
            // Tìm file log mới nhất
            var logFiles = Directory.GetFiles(_logDirectory, "socket_service_*.log")
                .OrderByDescending(f => File.GetCreationTime(f))
                .ToList();

            if (logFiles.Any())
            {
                var latestFile = logFiles.First();
                var fileInfo = new FileInfo(latestFile);

                // Nếu file chưa đạt 200MB, tiếp tục ghi vào file này
                if (fileInfo.Length < _maxFileSizeBytes)
                {
                    _currentLogFilePath = latestFile;
                    _currentFileSize = fileInfo.Length;
                    return;
                }
            }

            // Tạo file log mới
            CreateNewLogFile();
        }

        /// <summary>
        /// Tạo file log mới
        /// </summary>
        private void CreateNewLogFile()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _currentLogFilePath = Path.Combine(_logDirectory, $"socket_service_{timestamp}.log");
            _currentFileSize = 0;

            // Ghi header vào file mới
            string header = $@"
================================================================================
SOCKET SERVICE LOG FILE
Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
================================================================================

";
            File.WriteAllText(_currentLogFilePath, header, Encoding.UTF8);
            _currentFileSize = new FileInfo(_currentLogFilePath).Length;
        }

        /// <summary>
        /// Check và rolling file nếu vượt quá 200MB
        /// </summary>
        private void CheckAndRollFile()
        {
            if (_currentFileSize >= _maxFileSizeBytes)
            {
                // Ghi footer vào file cũ
                string footer = $@"
================================================================================
LOG FILE REACHED SIZE LIMIT (200MB)
Closed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
================================================================================
";
                File.AppendAllText(_currentLogFilePath, footer, Encoding.UTF8);

                // Tạo file mới
                CreateNewLogFile();
            }
        }

        /// <summary>
        /// Ghi log chung (thread-safe)
        /// </summary>
        private async Task WriteLogAsync(string level, string logId, string clientId, string message, Exception ex = null)
        {
            if (_disposed)
                return;

            await _fileLock.WaitAsync();
            try
            {
                CheckAndRollFile();

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [LogID: {logId}] [Client: {clientId}]");
                sb.AppendLine($"  {message}");

                if (ex != null)
                {
                    sb.AppendLine($"  Exception: {ex.GetType().Name}");
                    sb.AppendLine($"  Message: {ex.Message}");
                    sb.AppendLine($"  StackTrace: {ex.StackTrace}");

                    if (ex.InnerException != null)
                    {
                        sb.AppendLine($"  InnerException: {ex.InnerException.Message}");
                    }
                }

                string logEntry = sb.ToString();
                byte[] logBytes = Encoding.UTF8.GetBytes(logEntry);

                await File.AppendAllTextAsync(_currentLogFilePath, logEntry, Encoding.UTF8);
                _currentFileSize += logBytes.Length;
            }
            catch (Exception writeEx)
            {
                // Fallback: ghi vào Console nếu không ghi được file
                Console.WriteLine($"[ERROR] Failed to write log: {writeEx.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Log INFO
        /// </summary>
        public async Task LogInfo(string logId, string clientId, string message)
        {
            await WriteLogAsync("INFO", logId, clientId, message);
        }

        /// <summary>
        /// Log INFO (sync)
        /// </summary>
        public void LogInfoSync(string logId, string clientId, string message)
        {
            LogInfo(logId, clientId, message).Wait();
        }

        /// <summary>
        /// Log WARNING
        /// </summary>
        public async Task LogWarning(string logId, string clientId, string message)
        {
            await WriteLogAsync("WARNING", logId, clientId, message);
        }

        /// <summary>
        /// Log WARNING (sync)
        /// </summary>
        public void LogWarningSync(string logId, string clientId, string message)
        {
            LogWarning(logId, clientId, message).Wait();
        }

        /// <summary>
        /// Log ERROR
        /// </summary>
        public async Task LogError(string logId, string clientId, string message, Exception ex = null)
        {
            await WriteLogAsync("ERROR", logId, clientId, message, ex);
        }

        /// <summary>
        /// Log ERROR (sync)
        /// </summary>
        public void LogErrorSync(string logId, string clientId, string message, Exception ex = null)
        {
            LogError(logId, clientId, message, ex).Wait();
        }

        /// <summary>
        /// Log DEBUG
        /// </summary>
        public async Task LogDebug(string logId, string clientId, string message)
        {
            await WriteLogAsync("DEBUG", logId, clientId, message);
        }

        /// <summary>
        /// Log DEBUG (sync)
        /// </summary>
        public void LogDebugSync(string logId, string clientId, string message)
        {
            LogDebug(logId, clientId, message).Wait();
        }

        /// <summary>
        /// Ghi separator line (để phân cách các transaction)
        /// </summary>
        public async Task LogSeparator()
        {
            if (_disposed)
                return;

            await _fileLock.WaitAsync();
            try
            {
                CheckAndRollFile();

                string separator = "────────────────────────────────────────────────────────────────────────────────\n";
                byte[] separatorBytes = Encoding.UTF8.GetBytes(separator);

                await File.AppendAllTextAsync(_currentLogFilePath, separator, Encoding.UTF8);
                _currentFileSize += separatorBytes.Length;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Log transaction (request + response) - atomic write
        /// </summary>
        public async Task LogTransactionAsync(
            string logId,
            string clientId,
            string request,
            int requestSize,
            string response,
            long responseTime,
            bool success)
        {
            if (_disposed)
                return;

            await _fileLock.WaitAsync();
            try
            {
                CheckAndRollFile();

                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"TRANSACTION LOG | LogID: {logId}");
                sb.AppendLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"Client: {clientId}");
                sb.AppendLine("================================================================================");
                sb.AppendLine();

                sb.AppendLine("📨 REQUEST:");
                sb.AppendLine($"  Size: {requestSize} bytes");
                sb.AppendLine($"  Content:");
                sb.AppendLine(IndentText(request, 4));
                sb.AppendLine();

                sb.AppendLine($"📤 RESPONSE:");
                sb.AppendLine($"  Status: {(success ? "SUCCESS" : "ERROR")}");
                sb.AppendLine($"  Time: {responseTime}ms");
                sb.AppendLine($"  Size: {response?.Length ?? 0} bytes");
                sb.AppendLine($"  Content:");
                sb.AppendLine(IndentText(response ?? "", 4));
                sb.AppendLine();

                sb.AppendLine("================================================================================");
                sb.AppendLine();

                string logEntry = sb.ToString();
                byte[] logBytes = Encoding.UTF8.GetBytes(logEntry);

                await File.AppendAllTextAsync(_currentLogFilePath, logEntry, Encoding.UTF8);
                _currentFileSize += logBytes.Length;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Indent text với số khoảng trắng
        /// </summary>
        private string IndentText(string text, int spaces)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            string indent = new string(' ', spaces);
            return indent + text.Replace("\n", "\n" + indent);
        }

        /// <summary>
        /// Lấy thông tin về file log hiện tại
        /// </summary>
        public (string FilePath, long SizeBytes, double SizeMB) GetCurrentLogFileInfo()
        {
            if (string.IsNullOrEmpty(_currentLogFilePath))
                return (null, 0, 0);

            double sizeMB = _currentFileSize / (1024.0 * 1024.0);
            return (_currentLogFilePath, _currentFileSize, sizeMB);
        }

        /// <summary>
        /// Lấy danh sách tất cả file log
        /// </summary>
        public List<(string FilePath, long SizeBytes, DateTime CreatedTime)> GetAllLogFiles()
        {
            var logFiles = Directory.GetFiles(_logDirectory, "socket_service_*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Select(f => (f.FullName, f.Length, f.CreationTime))
                .ToList();

            return logFiles;
        }

        /// <summary>
        /// Cleanup old log files (giữ lại N files mới nhất)
        /// </summary>
        public async Task CleanupOldLogFilesAsync(int keepLatestCount = 10)
        {
            await _fileLock.WaitAsync();
            try
            {
                var logFiles = Directory.GetFiles(_logDirectory, "socket_service_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                // Xóa các file cũ, chỉ giữ lại keepLatestCount files mới nhất
                var filesToDelete = logFiles.Skip(keepLatestCount).ToList();

                foreach (var file in filesToDelete)
                {
                    try
                    {
                        // Không xóa file đang sử dụng
                        if (file.FullName != _currentLogFilePath)
                        {
                            file.Delete();
                        }
                    }
                    catch
                    {
                        // Ignore errors khi xóa file
                    }
                }
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _fileLock?.Dispose();
        }
    }
}