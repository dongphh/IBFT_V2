# Error Handling & Troubleshooting Guide
## IBFT Socket Service Request/Response Logging

---

## Table of Contents
1. [Common Errors & Solutions](#common-errors--solutions)
2. [Runtime Error Scenarios](#runtime-error-scenarios)
3. [File I/O Issues](#file-io-issues)
4. [Thread Safety & Concurrency](#thread-safety--concurrency)
5. [Logging Configuration](#logging-configuration)
6. [Debugging Tips](#debugging-tips)
7. [Performance Considerations](#performance-considerations)

---

## Common Errors & Solutions

### Error: "Error creating log directory"
**Cause**: The application lacks permissions to create the `logs/request-response` directory.

**Solution**:
```csharp
// Ensure the application has write permissions to the working directory
// Run the service with appropriate user privileges
// Or pre-create the directory with proper permissions
```

**Prevention**:
- Run the service with administrator privileges
- Pre-create the logs directory with appropriate permissions
- Ensure the service account has write access to the application directory

---

### Error: "Error writing to request/response log file"
**Cause**: File is locked by another process or disk is full.

**Solution**:
The `RequestResponseLogger` now includes automatic retry logic (3 retries with 100ms delays) to handle transient file locking issues.

```csharp
// The logger will automatically retry if IOException occurs
// If all retries fail, the error is logged and exception is thrown
```

**Prevention**:
- Monitor disk space regularly
- Ensure antivirus software doesn't lock log files
- Use file rotation to prevent log files from becoming too large

---

### Error: "Response is null or empty"
**Cause**: The `ProcessBankingRequestAsync` method returned null or empty string.

**Solution**:
The `SocketClientHandler` now validates response before sending:

```csharp
if (string.IsNullOrEmpty(response))
{
    _logger.LogError("Response is null or empty for LogID: {LogId}", logId);
    response = GenerateErrorResponse(logId, "Internal error: response generation failed");
}
```

**Prevention**:
- Ensure `ProcessBankingRequestAsync` always returns a valid response
- Add validation in response generation methods
- Test with various input scenarios

---

### Error: "Socket disposed during send"
**Cause**: Client disconnected while response was being sent.

**Solution**:
The handler catches `ObjectDisposedException` and logs the event:

```csharp
catch (ObjectDisposedException)
{
    _logger.LogWarning("Socket disposed during send for {ClientId}", clientId);
    throw;
}
```

**Prevention**:
- Implement connection keep-alive mechanisms
- Monitor client connection status before sending
- Use appropriate timeout values

---

## Runtime Error Scenarios

### Scenario 1: High Concurrency with File Locking
**Problem**: Multiple clients logging simultaneously causes file access conflicts.

**Solution Implemented**:
- `SemaphoreSlim(1, 1)` ensures only one thread writes to the log file at a time
- Automatic retry logic handles transient locks
- Async file operations prevent blocking

```csharp
await _semaphore.WaitAsync();
try
{
    // File write operation
}
finally
{
    _semaphore.Release();
}
```

---

### Scenario 2: Log Directory Doesn't Exist
**Problem**: Application starts before log directory is created.

**Solution Implemented**:
- Constructor creates directory if it doesn't exist
- `AppendToFileAsync` double-checks directory existence before writing

```csharp
if (!Directory.Exists(_logDirectory))
{
    Directory.CreateDirectory(_logDirectory);
}
```

---

### Scenario 3: Null Logger Injection
**Problem**: `RequestResponseLogger` receives null `ILogger<RequestResponseLogger>`.

**Solution Implemented**:
- Constructor validates logger parameter:

```csharp
_logger = logger ?? throw new ArgumentNullException(nameof(logger));
```

---

### Scenario 4: Response Generation Failure
**Problem**: `ProcessBankingRequestAsync` throws exception, response is null.

**Solution Implemented**:
- Exception is caught and error response is generated
- Null check before sending response
- Fallback error response generation

```csharp
catch (Exception ex)
{
    sw.Stop();
    success = false;
    _logger.LogError(ex, "Error processing request LogID: {LogId}", logId);
    response = GenerateErrorResponse(logId, ex.Message);
}
```

---

## File I/O Issues

### Issue: Log File Becomes Too Large
**Symptom**: Slow file writes, high disk usage.

**Solution**:
Implement log rotation in Serilog configuration (already configured in `Program.cs`):

```csharp
.WriteTo.File("logs/request-response-.log",
    rollingInterval: RollingInterval.Day,
    retainedFileCountLimit: 30)
```

---

### Issue: Disk Space Exhausted
**Symptom**: "Error writing to request/response log file" with IOException.

**Solution**:
1. Monitor disk space
2. Implement log cleanup policy
3. Archive old logs

```csharp
// Add to IBFT_V2_Service or separate maintenance service
private void CleanupOldLogs()
{
    var logDir = new DirectoryInfo("logs/request-response");
    var oldFiles = logDir.GetFiles("*.log")
        .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-30))
        .ToList();
    
    foreach (var file in oldFiles)
    {
        file.Delete();
    }
}
```

---

### Issue: File Locked by Antivirus
**Symptom**: Intermittent "file is locked" errors.

**Solution**:
- Exclude log directory from real-time scanning
- Use retry logic (already implemented)
- Consider using a separate logging service

---

## Thread Safety & Concurrency

### Semaphore Implementation
The `RequestResponseLogger` uses `SemaphoreSlim` to ensure thread-safe file access:

```csharp
private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

public async Task LogRequestAsync(...)
{
    await _semaphore.WaitAsync();
    try
    {
        // Only one thread can execute this at a time
    }
    finally
    {
        _semaphore.Release();
    }
}
```

### Race Condition Prevention
- File path is calculated fresh each time to handle day rollovers
- Semaphore prevents concurrent writes
- Async operations don't block thread pool

---

## Logging Configuration

### Serilog Configuration (Program.cs)
```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/socket-service-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateLogger();
```

### Log Levels
- **Information**: Normal operations, client connections, request/response logging
- **Warning**: Timeouts, connection issues, disposal errors
- **Error**: Processing failures, I/O errors, null references
- **Debug**: Socket configuration, receive timeouts

---

## Debugging Tips

### Enable Debug Logging
Modify `Program.cs` to include Debug level:

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()  // Changed from Information
    .WriteTo.Console(...)
    .WriteTo.File(...)
    .CreateLogger();
```

### Check Log Files
```bash
# View recent request/response logs
Get-Content "logs/request-response/request-response-2025-11-25.log" -Tail 50

# View service logs
Get-Content "logs/socket-service-2025-11-25.txt" -Tail 50
```

### Monitor File Access
```csharp
// Add to RequestResponseLogger for debugging
_logger.LogDebug("Attempting to write to log file: {FilePath}", currentLogFile);
_logger.LogDebug("Log directory exists: {Exists}", Directory.Exists(_logDirectory));
```

### Trace Request/Response Flow
Each request has a unique LogID that appears in:
1. Console logs
2. Service log file
3. Request/response log file
4. Database (when enabled)

Use LogID to correlate all related logs:
```bash
# Find all logs for a specific request
Select-String "LogID: ABC123DEF456" logs/socket-service-*.txt
Select-String "ABC123DEF456" logs/request-response/*.log
```

---

## Performance Considerations

### Async File Operations
The logger uses async file operations to prevent blocking:

```csharp
using (var fileStream = new FileStream(..., useAsync: true))
{
    await fileStream.WriteAsync(buffer, 0, buffer.Length);
    await fileStream.FlushAsync();
}
```

### Semaphore Contention
With high concurrency, semaphore contention may occur. Monitor with:

```csharp
// Add metrics to track wait times
var sw = Stopwatch.StartNew();
await _semaphore.WaitAsync();
sw.Stop();
_logger.LogDebug("Semaphore wait time: {WaitMs}ms", sw.ElapsedMilliseconds);
```

### Buffer Size
File stream uses 4096-byte buffer (default). Adjust if needed:

```csharp
using (var fileStream = new FileStream(
    currentLogFile,
    FileMode.Append,
    FileAccess.Write,
    FileShare.Read,
    8192,  // Increase buffer size for high throughput
    useAsync: true))
```

### Retry Strategy
Current retry configuration:
- Max retries: 3
- Delay between retries: 100ms
- Total max wait: ~300ms

Adjust based on your environment:

```csharp
const int maxRetries = 5;        // Increase for more resilience
const int retryDelayMs = 200;    // Increase for slower systems
```

---

## Error Recovery Checklist

When encountering errors:

- [ ] Check disk space: `Get-Volume`
- [ ] Verify log directory permissions: `icacls logs`
- [ ] Check for locked files: `Get-Process | Where-Object {$_.Handles -gt 1000}`
- [ ] Review recent logs for patterns
- [ ] Verify network connectivity (for database operations)
- [ ] Check service account permissions
- [ ] Monitor CPU and memory usage
- [ ] Review event viewer for system errors
- [ ] Test with load tester to reproduce issues
- [ ] Enable debug logging for detailed diagnostics

---

## Contact & Support

For issues related to:
- **Request/Response Logging**: Check `RequestResponseLogger.cs`
- **Socket Operations**: Check `SocketClientHandler.cs`
- **Service Configuration**: Check `IBFT_V2_Service.cs`
- **Logging Setup**: Check `Program.cs`

---

**Last Updated**: 2025-11-25
**Version**: 1.0
