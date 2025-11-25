# Request/Response Logging Feature

## Overview

The `RequestResponseLogger` service provides dedicated logging for all incoming requests and outgoing responses in the IBFT Socket Service. This feature logs complete transaction pairs to separate log files for easy auditing and debugging.

## Features

- **Dedicated Log Files**: Request/response pairs are logged to separate files in `logs/request-response/` directory
- **Daily Log Rotation**: New log files are created daily with format `request-response-YYYY-MM-DD.log`
- **Thread-Safe Logging**: Uses `SemaphoreSlim` to ensure thread-safe file writes
- **Unique Transaction IDs**: Each request/response pair is tracked with a unique LogID
- **Comprehensive Formatting**: Logs include:
  - Timestamp (UTC)
  - LogID (unique identifier for the transaction)
  - ClientID (remote endpoint)
  - Request size in bytes
  - Response status (SUCCESS/ERROR)
  - Response time in milliseconds
  - Full request and response content

## Log File Location

```
logs/request-response/request-response-2025-11-25.log
logs/request-response/request-response-2025-11-26.log
...
```

## Log Format

### Transaction Log Format

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
║   ...
║ </request>
╠════════════════════════════════════════════════════════════════════════════════╣
║ RESPONSE
╠────────────────────────────────────────────────────────────────────────────────╣
║ Status: SUCCESS
║ Response Time: 125ms
║ Size: 256 bytes
║
║ <response>
║   <header>
║     <external_ref>REF123456</external_ref>
║     <timestamp>2025-11-25 10:30:45</timestamp>
║   </header>
║   <body>
║     <status>SUCCESS</status>
║     <message>SUCCESS</message>
║     <code>00</code>
║   </body>
║ </response>
╚════════════════════════════════════════════════════════════════════════════════╝
```

## Integration Points

### 1. Service Registration (Program.cs)

```csharp
// Register request/response logger
services.AddSingleton<RequestResponseLogger>();
```

### 2. Dependency Injection

The `RequestResponseLogger` is injected into:
- `IBFT_V2_Service` - Main socket service
- `SocketClientHandler` - Individual client connection handler

### 3. Usage in SocketClientHandler

```csharp
// Log complete transaction after processing
await _requestResponseLogger.LogTransactionAsync(
    logId,           // Unique transaction ID
    clientId,        // Client IP:Port
    request,         // Request XML content
    bytesRead,       // Request size
    response,        // Response XML content
    responseTimeMs,  // Processing time
    success          // Success/Error status
);
```

## API Methods

### LogRequestAsync
Logs only the request portion of a transaction.

```csharp
public async Task LogRequestAsync(
    string logId,
    string clientId,
    string requestContent,
    int requestSize)
```

### LogResponseAsync
Logs only the response portion of a transaction.

```csharp
public async Task LogResponseAsync(
    string logId,
    string clientId,
    string responseContent,
    long responseTimeMs,
    bool success)
```

### LogTransactionAsync
Logs a complete request/response pair in a single formatted entry.

```csharp
public async Task LogTransactionAsync(
    string logId,
    string clientId,
    string requestContent,
    int requestSize,
    string responseContent,
    long responseTimeMs,
    bool success)
```

## Configuration

The logger automatically:
1. Creates the `logs/request-response/` directory if it doesn't exist
2. Creates daily log files with UTC timestamps
3. Handles file I/O asynchronously to avoid blocking
4. Manages concurrent writes with semaphore-based locking

## Error Handling

If an error occurs during logging:
- The error is logged to the main application logger
- The error does not propagate to the request/response processing
- The service continues to operate normally

## Performance Considerations

- **Async I/O**: All file operations are asynchronous to prevent blocking
- **Semaphore Locking**: Ensures thread-safe writes without excessive contention
- **Buffered Writing**: Uses 4KB buffer for efficient file I/O
- **Minimal Overhead**: Logging is performed after response is sent to client

## Monitoring

To monitor request/response logs in real-time:

```bash
# Windows PowerShell
Get-Content -Path "logs/request-response/request-response-2025-11-25.log" -Wait

# Linux/Mac
tail -f logs/request-response/request-response-2025-11-25.log
```

## Log Retention

Log files are retained indefinitely. To manage disk space, consider:
1. Archiving old log files
2. Implementing a cleanup policy
3. Using log aggregation services

## Troubleshooting

### Logs not being created
- Check that the application has write permissions to the `logs/` directory
- Verify the `RequestResponseLogger` is registered in `Program.cs`
- Check the main application log for errors

### Missing request/response data
- Verify that `LogTransactionAsync` is being called in `SocketClientHandler.ReceiveAndProcessAsync`
- Check that the `RequestResponseLogger` is properly injected

### Performance issues
- Monitor file I/O performance
- Consider archiving old log files
- Check available disk space
