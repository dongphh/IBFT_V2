# Unified Logging Configuration

## Overview

The IBFT Socket Service now implements a unified logging system that consolidates all logs into a single file with automatic rolling at 200MB intervals. This ensures complete request/response transaction logging without interleaving from concurrent requests.

## Key Features

### 1. **Single Unified Log File**
- All logs are written to: `logs/unified.log`
- When the file reaches 200MB, it automatically rolls to `logs/unified.1.log`, `logs/unified.2.log`, etc.
- Maximum of 10 rolled files are retained (oldest files are deleted)

### 2. **Atomic Request/Response Logging**
- Each request/response transaction is logged as a complete, uninterrupted block
- Uses `SemaphoreSlim` to ensure thread-safe, atomic writes
- No interleaving of logs from concurrent client connections
- Complete transaction visibility with clear visual separators

### 3. **Serilog Configuration**
Located in [`Program.cs`](Program.cs:14-23):

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/unified.log",
        rollingInterval: RollingInterval.Infinite,
        fileSizeLimitBytes: 200 * 1024 * 1024, // 200MB
        retainedFileCountLimit: 10,
        rollOnFileSizeLimit: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateLogger();
```

## Components

### UnifiedLogger Service
**File:** [`UnifiedLogger.cs`](UnifiedLogger.cs)

The `UnifiedLogger` class provides:
- `LogTransactionAsync()` - Logs complete request/response pairs atomically
- `LogMessageAsync()` - Logs general messages with optional LogID
- Automatic file rolling at 200MB
- Retry logic for file I/O operations
- Thread-safe operations using `SemaphoreSlim`

### Integration Points

1. **Program.cs** - Registers `UnifiedLogger` as a singleton service
2. **IBFT_V2_Service.cs** - Injects `UnifiedLogger` and passes it to client handlers
3. **SocketClientHandler.cs** - Uses `UnifiedLogger` for atomic transaction logging

## Log Format

### Transaction Log Entry
```
╔════════════════════════════════════════════════════════════════════════════════╗
║ TRANSACTION LOG | Timestamp: 2025-11-25 10:30:45.123
║ LogID: A1B2C3D4E5F6G7H8
║ ClientID: 192.168.1.100:54321
╠════════════════════════════════════════════════════════════════════════════════╣
║ REQUEST
╠────────────────────────────────────────────────────────────────────────────────╣
║ Size: 512 bytes
║
║ <request>
║   <tcode>SWIBFTTSF</tcode>
║   <external_ref>REF123456</external_ref>
║ </request>
╠════════════════════════════════════════════════════════════════════════════════╣
║ RESPONSE
╠────────────────────────────────────────────────────────────────────────────────╣
║ Status: SUCCESS
║ Response Time: 125ms
║ Size: 256 bytes
║
║ <response>
║   <status>SUCCESS</status>
║   <message>SUCCESS</message>
║ </response>
╚════════════════════════════════════════════════════════════════════════════════╝
```

### General Log Entry
```
[2025-11-25 10:30:45.123] [INF] | LogID: A1B2C3D4E5F6G7H8 Message content here
```

## File Rolling Behavior

### Automatic Rolling
- When `logs/unified.log` reaches 200MB (209,715,200 bytes)
- File is automatically renamed to `logs/unified.1.log`
- New `logs/unified.log` is created for new entries
- Previous rolled files are renamed: `unified.1.log` → `unified.2.log`, etc.

### Retention Policy
- Maximum 10 rolled files are kept
- Oldest files are automatically deleted when limit is exceeded
- Example: If you have `unified.1.log` through `unified.10.log`, creating `unified.11.log` will delete `unified.10.log`

## Thread Safety

The `UnifiedLogger` ensures thread-safe operations:

1. **SemaphoreSlim Lock** - Only one write operation at a time
2. **Atomic Writes** - Complete transaction logged without interruption
3. **Retry Logic** - Handles transient I/O failures (3 retries with 100ms delay)

## Usage Example

```csharp
// In SocketClientHandler.cs
await _unifiedLogger.LogTransactionAsync(
    logId: "A1B2C3D4E5F6G7H8",
    clientId: "192.168.1.100:54321",
    requestContent: requestXml,
    requestSize: 512,
    responseContent: responseXml,
    responseTimeMs: 125,
    success: true
);
```

## Monitoring Log Files

### View Current Log
```bash
# Windows
type logs\unified.log

# Linux/Mac
cat logs/unified.log
```

### Monitor Log Size
```bash
# Windows
dir logs\unified.log

# Linux/Mac
ls -lh logs/unified.log
```

### Search for Specific Transaction
```bash
# Windows
findstr "A1B2C3D4E5F6G7H8" logs\unified.log

# Linux/Mac
grep "A1B2C3D4E5F6G7H8" logs/unified.log
```

## Performance Considerations

1. **Async I/O** - All file operations are asynchronous
2. **Buffered Writing** - 4KB buffer for efficient I/O
3. **Minimal Lock Contention** - SemaphoreSlim ensures fair queuing
4. **No Blocking** - Logging failures don't crash the service

## Troubleshooting

### Issue: Log file not being created
- Check that `logs/` directory exists or can be created
- Verify write permissions on the logs directory
- Check application startup logs for errors

### Issue: Logs are interleaved
- This should not happen with the current implementation
- If observed, check that `UnifiedLogger` is being used (not `RequestResponseLogger`)
- Verify `SemaphoreSlim` is properly initialized

### Issue: File rolling not working
- Verify `fileSizeLimitBytes` is set to 200MB (209,715,200 bytes)
- Check that `rollOnFileSizeLimit: true` is configured
- Ensure sufficient disk space for new log files

## Migration from Old Logging

The old `RequestResponseLogger` is kept for backward compatibility but is no longer used for transaction logging. All new transaction logs go to the unified log file.

To completely remove the old logger:
1. Remove `RequestResponseLogger` from dependency injection in `Program.cs`
2. Remove the `_requestResponseLogger` field from `SocketClientHandler`
3. Delete `IBFTSocketService/Services/RequestResponseLogger.cs`

## Configuration Reference

| Setting | Value | Purpose |
|---------|-------|---------|
| Log File | `logs/unified.log` | Main log file path |
| File Size Limit | 200MB (209,715,200 bytes) | Trigger for file rolling |
| Rolling Interval | Infinite | No time-based rolling, only size-based |
| Retained Files | 10 | Maximum number of rolled files to keep |
| Roll on Size Limit | true | Enable automatic rolling |
| Timestamp Format | `yyyy-MM-dd HH:mm:ss.fff` | Millisecond precision |
| Log Level | Information | Minimum log level |

## Future Enhancements

Potential improvements:
- Compression of rolled log files
- Remote log aggregation
- Real-time log streaming
- Advanced filtering and search capabilities
- Performance metrics per transaction
