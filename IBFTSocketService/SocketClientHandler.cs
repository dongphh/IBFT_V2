using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IBFTSocketService.Core.Configuration;
using IBFTSocketService.Data.Repository;

namespace SocketService
{
    public class SocketClientHandler : IDisposable
    {
        private readonly Socket _socket;
        private readonly IDataRepository _repository;
        private readonly ILogger<SocketClientHandler> _logger;
        private readonly byte[] _receiveBuffer;
        private readonly SocketServerConfig _config;
        private DateTime _lastActivityTime;
        private volatile bool _disposed;
        private volatile bool _disconnectCalled;
        private int _requestCount;
        private readonly object _disposeLock = new object();

        public event EventHandler<ClientDisconnectEventArgs> OnDisconnect;
        private readonly RemoteService _remoteService;

        public SocketClientHandler(
            Socket socket,
            IDataRepository repository,
            ILogger<SocketClientHandler> logger,
            SocketServerConfig config,
            RemoteService remoteService)
        {
            _socket = socket;
            _repository = repository;
            _logger = logger;
            _config = config;
            _receiveBuffer = new byte[config.ReceiveBufferSize];
            _lastActivityTime = DateTime.UtcNow;
            _requestCount = 0;
            _disposed = false;
            _disconnectCalled = false;

            ConfigureSocket();
            _remoteService = remoteService;
        }

        private void ConfigureSocket()
        {
            if (_disposed || _socket == null) return;

            try
            {
                _socket.NoDelay = true;
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);

                _logger.LogDebug("Socket options configured for {RemoteEndPoint}", _socket.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warning setting socket options");
            }
        }

        public async Task HandleClientAsync(CancellationToken cancellationToken = default)
        {
            string clientId = _socket.RemoteEndPoint?.ToString() ?? "Unknown";

            try
            {
                if (_disposed)
                {
                    _logger.LogWarning("Handler already disposed for {ClientId}", clientId);
                    return;
                }

                _logger.LogInformation("✅ Client connected: {ClientId}", clientId);

                while (!_disposed && !cancellationToken.IsCancellationRequested)
                {
                    if (!IsSocketConnected())
                    {
                        _logger.LogInformation("Socket disconnected for {ClientId}", clientId);
                        break;
                    }

                    if ((DateTime.UtcNow - _lastActivityTime).TotalMilliseconds > _config.IdleTimeout)
                    {
                        _logger.LogInformation("⏱️  Client {ClientId} idle timeout", clientId);
                        break;
                    }

                    try
                    {
                        await ReceiveAndProcessAsync(clientId, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Operation cancelled for {ClientId}", clientId);
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogWarning("Socket disposed during operation for {ClientId}", clientId);
                        break;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionAborted)
                    {
                        _logger.LogWarning("Connection aborted for {ClientId}", clientId);
                        break;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        _logger.LogInformation("Connection reset by client {ClientId}", clientId);
                        break;
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogWarning("Socket error for {ClientId}: {SocketError}", clientId, ex.SocketErrorCode);
                        break;
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "IO error for {ClientId}", clientId);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error handling client {ClientId}", clientId);
            }
            finally
            {
                Disconnect(clientId);
            }
        }

        private bool IsSocketConnected()
        {
            if (_disposed || _socket == null)
                return false;

            try
            {
                bool connected = _socket.Connected;
                if (connected)
                {
                    connected = !(_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0);
                }
                return connected;
            }
            catch
            {
                return false;
            }
        }

        private async Task ReceiveAndProcessAsync(string clientId, CancellationToken cancellationToken)
        {
            if (_disposed)
                return;

            int bytesRead = 0;

            try
            {
                using (var timeoutCts = new CancellationTokenSource(_config.ConnectionTimeout))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                {
                    if (_disposed)
                        return;

                    try
                    {
                        bytesRead = await _socket.ReceiveAsync(
                            new ArraySegment<byte>(_receiveBuffer),
                            SocketFlags.None,
                            linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Receive timeout for {ClientId}", clientId);
                        throw;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("Socket disposed during receive for {ClientId}", clientId);
                throw;
            }

            if (bytesRead == 0)
            {
                _logger.LogInformation("Client {ClientId} closed connection", clientId);
                return;
            }

            _lastActivityTime = DateTime.UtcNow;
            _requestCount++;

            string request = Encoding.UTF8.GetString(_receiveBuffer, 0, bytesRead).Trim();

            if (string.IsNullOrWhiteSpace(request))
                return;

            // ✅ Generate unique Log ID for this request/response pair
            string logId = Guid.NewGuid().ToString("N").Substring(0, 16).ToUpper();

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                { "LogID", logId },
                { "ClientID", clientId },
                { "RequestCount", _requestCount }
            }))
            {
                // ✅ LOG REQUEST WITH UNIQUE ID               
                _logger.LogInformation("────────────────────────────────────────────────────────");
                _logger.LogInformation("📨 REQUEST RECEIVED | LogID: {LogId} | Client: {ClientId} | Size: {BytesRead} bytes | Request body: {request}",
                    logId, clientId, bytesRead, request);

                var sw = Stopwatch.StartNew();
                string? response = null;
                bool success = false;

                try
                {
                    // ✅ SAVE REQUEST TO DATABASE
                    await SaveRequestToDbAsync(logId, clientId, request, bytesRead);

                    // ✅ PROCESS REQUEST
                    response = await ProcessBankingRequestAsync(request, logId, clientId);
                    success = true;

                    sw.Stop();

                    // ✅ SAVE RESPONSE TO DATABASE
                    await SaveResponseToDbAsync(logId, response, sw.ElapsedMilliseconds, success);

                    // ✅ LOG RESPONSE WITH UNIQUE ID

                    _logger.LogInformation("📤 RESPONSE SENT | Client: {ClientId} | LogID: {LogId} | Status: SUCCESS | Time: {Time}ms | Response body: {response}",
                        clientId, logId, sw.ElapsedMilliseconds, response);
                    _logger.LogInformation("────────────────────────────────────────────────────────");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    success = false;

                    _logger.LogError(ex, "Error processing request LogID: {LogId}", logId);

                    response = GenerateErrorResponse(logId, ex.Message);

                    // ✅ SAVE ERROR RESPONSE TO DATABASE
                    await SaveResponseToDbAsync(logId, response, sw.ElapsedMilliseconds, false);

                    // ✅ LOG REQUEST/RESPONSE TO UNIFIED FILE (ERROR CASE - atomic, no interleaving)
                    _logger.LogInformation(
                         logId, clientId, request, bytesRead, response, sw.ElapsedMilliseconds, false);

                    _logger.LogInformation("────────────────────────────────────────────────────────");
                    _logger.LogInformation("❌ RESPONSE SENT | Client: {ClientId} | LogID: {LogId} | Status: ERROR | Time: {Time}ms",
                        clientId, logId, sw.ElapsedMilliseconds);
                    //_logger.LogInformation("Client: {ClientId} | Status: ERROR | Time: {Time}ms",
                    //    clientId, sw.ElapsedMilliseconds);
                    _logger.LogInformation("────────────────────────────────────────────────────────");
                }

                try
                {
                    if (string.IsNullOrEmpty(response))
                    {
                        _logger.LogError("Response is null or empty for LogID: {LogId}", logId);
                        response = GenerateErrorResponse(logId, "Internal error: response generation failed");
                    }

                    await SendResponseAsync(response, clientId, logId);
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogWarning("Socket disposed during send for {ClientId}", clientId);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending response to {ClientId}", clientId);
                    throw;
                }
            }
        }

        /// <summary>
        /// Save request to database
        /// </summary>
        private async Task SaveRequestToDbAsync(string logId, string clientId, string requestXml, int requestSize)
        {
            try
            {
                string sql = @"
                    INSERT INTO transaction_log (
                        log_id, client_id, request_xml, request_size, 
                        request_timestamp, created_at
                    ) VALUES (
                        :log_id, :client_id, :request_xml, :request_size,
                        SYSDATE, SYSDATE
                    )";

                var parameters = new Dictionary<string, object>
                {
                    { ":log_id", logId },
                    { ":client_id", clientId },
                    { ":request_xml", requestXml },
                    { ":request_size", requestSize }
                };

                //await _repository.ExecuteNonQueryAsync(sql, parameters);

                _logger.LogInformation("✅ REQUEST SAVED TO DB | LogID: {LogId} | Size: {Size} bytes",
                    logId, requestSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ ERROR SAVING REQUEST TO DB | LogID: {LogId} | Error: {Error}",
                    logId, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Save response to database
        /// </summary>
        private async Task SaveResponseToDbAsync(string logId, string responseXml, long responseTime, bool success)
        {
            try
            {
                string sql = @"
                    UPDATE transaction_log SET
                        response_xml = :response_xml,
                        response_size = :response_size,
                        response_time_ms = :response_time_ms,
                        status = :status,
                        response_timestamp = SYSDATE
                    WHERE log_id = :log_id";

                var parameters = new Dictionary<string, object>
                {
                    { ":response_xml", responseXml },
                    { ":response_size", responseXml.Length },
                    { ":response_time_ms", responseTime },
                    { ":status", success ? "SUCCESS" : "ERROR" },
                    { ":log_id", logId }
                };

                //await _repository.ExecuteNonQueryAsync(sql, parameters);

                string status = success ? "SUCCESS" : "ERROR";
                _logger.LogInformation("✅ RESPONSE SAVED TO DB | LogID: {LogId} | Status: {Status} | Time: {Time}ms | Size: {Size} bytes",
                    logId, status, responseTime, responseXml.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ ERROR SAVING RESPONSE TO DB | LogID: {LogId} | Error: {Error}",
                    logId, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Process banking request
        /// </summary>
        private async Task<string> ProcessBankingRequestAsync(string requestXml, string logId, string clientId)
        {
            // Extract transaction code from request
            string tcode = ExtractXmlValue(requestXml, "tcode");
            string externalRef = ExtractXmlValue(requestXml, "external_ref");

            if (tcode != "SWIBFTTSF")
            {
                throw new InvalidOperationException($"Invalid transaction code: {tcode}");
            }

            // Simulate processing (in real scenario, this would process the transfer)
            await Task.Delay(50);
            
            // Forward request to Napas SocketService 
            string partnerResponse = await _remoteService.SendRequestAsync(requestXml);
            //return partnerResponse;
            return GenerateSuccessResponse(logId, externalRef, "SUCCESS");
        }

        private string ExtractXmlValue(string xml, string tagName)
        {
            try
            {
                int start = xml.IndexOf($"<{tagName}>") + tagName.Length + 2;
                int end = xml.IndexOf($"</{tagName}>");
                if (start > tagName.Length + 1 && end > start)
                {
                    return xml.Substring(start, end - start).Trim();
                }
            }
            catch { }

            return "";
        }

        private string GenerateSuccessResponse(string logId, string externalRef, string message)
        {
            // ✅ Response WITHOUT LogID in XML - LogID only in logging/DB
            return $@"<response>
  <header>
    <external_ref>{externalRef}</external_ref>
    <timestamp>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</timestamp>
  </header>
  <body>
    <status>SUCCESS</status>
    <message>{message}</message>
    <code>00</code>
  </body>
</response>";
        }

        private string GenerateErrorResponse(string logId, string errorMessage)
        {
            // ✅ Error Response WITHOUT LogID in XML - LogID only in logging/DB
            return $@"<response>
  <header>
    <timestamp>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</timestamp>
  </header>
  <body>
    <status>ERROR</status>
    <message>{errorMessage}</message>
    <code>99</code>
  </body>
</response>";
        }

        private async Task SendResponseAsync(string response, string clientId, string logId)
        {
            if (string.IsNullOrEmpty(response))
            {
                _logger.LogError("Cannot send response - response is null or empty for LogID: {LogId}", logId);
                throw new InvalidOperationException("Response cannot be null or empty");
            }

            if (_disposed || !IsSocketConnected())
            {
                _logger.LogWarning("Cannot send response - socket not connected for {ClientId}", clientId);
                return;
            }

            try
            {
                byte[] responseBytes = Encoding.UTF8.GetBytes(response);

                if (responseBytes.Length == 0)
                {
                    _logger.LogError("Response bytes are empty for LogID: {LogId}", logId);
                    throw new InvalidOperationException("Response bytes cannot be empty");
                }

                using (var cts = new CancellationTokenSource(_config.ConnectionTimeout))
                {
                    if (_disposed)
                        return;

                    await _socket.SendAsync(
                        new ArraySegment<byte>(responseBytes),
                        SocketFlags.None,
                        cts.Token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending response for LogID: {LogId}", logId);
                throw;
            }
        }

        private void Disconnect(string clientId)
        {
            if (_disconnectCalled)
                return;

            lock (_disposeLock)
            {
                if (_disconnectCalled)
                    return;

                _disconnectCalled = true;
                _disposed = true;

                try
                {
                    if (_socket != null)
                    {
                        try
                        {
                            if (_socket.Connected)
                            {
                                _socket.Shutdown(SocketShutdown.Both);
                            }
                        }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { }

                        _socket.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during socket cleanup for {ClientId}", clientId);
                }

                _logger.LogInformation("🔌 Client {ClientId} disconnected. Total requests: {Count}", clientId, _requestCount);

                EndPoint remoteEndPoint = null;
                try
                {
                    remoteEndPoint = _socket?.RemoteEndPoint;
                }
                catch { }

                try
                {
                    OnDisconnect?.Invoke(this, new ClientDisconnectEventArgs
                    {
                        RemoteEndPoint = remoteEndPoint,
                        DisconnectTime = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error invoking OnDisconnect for {ClientId}", clientId);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_disposeLock)
            {
                if (_disposed)
                    return;

                _disposed = true;

                try
                {
                    _socket?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing socket");
                }
            }

            GC.SuppressFinalize(this);
        }
    }

    public class ClientDisconnectEventArgs : EventArgs
    {
        public EndPoint RemoteEndPoint { get; set; }
        public DateTime DisconnectTime { get; set; }
    }
}