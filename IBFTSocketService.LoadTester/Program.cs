using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace SocketService.LoadTester
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var options = ParseArguments(args);

            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   Banking Transfer Load Tester (with Unique Log ID)   ║");
            Console.WriteLine("╠════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║ Host:              {options.Host,-42} ║");
            Console.WriteLine($"║ Port:              {options.Port,-42} ║");
            Console.WriteLine($"║ Concurrent Clients:{options.ConcurrentClients,-42} ║");
            Console.WriteLine($"║ Duration (seconds):{options.DurationSeconds,-42} ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");

            var tester = new LoadTester(options);
            await tester.RunAsync();
        }

        private static LoadTestOptions ParseArguments(string[] args)
        {
            var options = new LoadTestOptions();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--host":
                        options.Host = args[++i];
                        break;
                    case "--port":
                        options.Port = int.Parse(args[++i]);
                        break;
                    case "--concurrent":
                        options.ConcurrentClients = int.Parse(args[++i]);
                        break;
                    case "--duration":
                        options.DurationSeconds = int.Parse(args[++i]);
                        break;
                }
            }

            return options;
        }
    }

    public class LoadTestOptions
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 5000;
        public int ConcurrentClients { get; set; } = 10;
        public int DurationSeconds { get; set; } = 60;
    }

    /// <summary>
    /// Banking XML Request Generator - Generates realistic banking transfer requests
    /// </summary>
    public class BankingRequestGenerator
    {
        private static int _transactionSeq = 40316762;
        private static readonly object _seqLock = new object();

        public static string GenerateTransferRequest()
        {
            int tcode = GetNextTransactionSeq();
            string externalRef = tcode.ToString().PadLeft(12, '0');
            string sysTrace = tcode.ToString().PadLeft(6, '0');

            // ✅ XML request WITHOUT LogID - LogID only in logging
            string xml = $@"<request>
  <header>
    <tcode>SWIBFTTSF</tcode>
    <term_id>HOMEBANKING</term_id>
    <external_ref>{externalRef}</external_ref>
  </header>
  <body>
    <card_no>1067040792654</card_no>
    <amount>111111</amount>
    <sender_dt>0802084449</sender_dt>
    <sender_dt_gmt>0802014449</sender_dt_gmt>
    <tran_ccy>704</tran_ccy>
    <src_channel>04</src_channel>
    <sys_trace>{sysTrace}</sys_trace>
    <bnb_code>970406</bnb_code>
    <bnb_name>NH TMCP DONG A</bnb_name>
    <dest_type>ACC</dest_type>
    <dest_number>0129837294</dest_number>
    <dest_name>NGUYEN VAN NAPAS</dest_name>
    <forward_route>BANKNET</forward_route>
    <narrative>DO THI SINH CHUYEN TIEN</narrative>
    <src_name>DO THI SINH</src_name>
    <tran_code>1111111</tran_code>
    <auth_otp>1111111</auth_otp>
    <auth_type>S</auth_type>
    <input_tran_seq>{tcode}</input_tran_seq>
  </body>
</request>";

            return xml;
        }

        private static int GetNextTransactionSeq()
        {
            lock (_seqLock)
            {
                return ++_transactionSeq;
            }
        }
    }

    public class LoadTester
    {
        private readonly LoadTestOptions _options;
        private readonly List<TestMetrics> _metrics = new();
        private readonly object _metricsLock = new object();

        public LoadTester(LoadTestOptions options)
        {
            _options = options;
        }

        public async Task RunAsync()
        {
            Console.WriteLine("[INFO] Starting load test...\n");

            var sw = Stopwatch.StartNew();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.DurationSeconds));
            var tasks = new List<Task>();

            // Start all clients
            for (int i = 0; i < _options.ConcurrentClients; i++)
            {
                tasks.Add(RunClientAsync(i, cts.Token));
            }

            // Status reporting
            var statusTask = ReportStatusAsync(cts.Token);

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n[INFO] Test duration completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[ERROR] Test failed: {ex.Message}");
            }
            finally
            {
                cts.Cancel();
            }

            sw.Stop();
            await Task.Delay(1000);
            await PrintResults(sw);
        }

        private async Task RunClientAsync(int clientId, CancellationToken cancellationToken)
        {
            TcpClient client = null;
            int requestCount = 0;
            int errorCount = 0;

            try
            {
                client = new TcpClient
                {
                    NoDelay = true,
                    SendBufferSize = 32768,
                    ReceiveBufferSize = 32768,
                    ReceiveTimeout = 30000,
                    SendTimeout = 30000
                };

                // Connect with timeout
                using (var connectCts = new CancellationTokenSource(10000))
                {
                    try
                    {
                        await client.ConnectAsync(_options.Host, _options.Port, connectCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"[CLIENT {clientId}] Connection timeout");
                        errorCount++;
                        return;
                    }
                }

                Console.WriteLine($"[CLIENT {clientId}] Connected to {_options.Host}:{_options.Port}");

                using (var stream = client.GetStream())
                {
                    stream.WriteTimeout = 30000;
                    stream.ReadTimeout = 30000;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Generate banking XML request
                            string request = BankingRequestGenerator.GenerateTransferRequest();
                            byte[] requestBytes = Encoding.UTF8.GetBytes(request);

                            var sw = Stopwatch.StartNew();

                            // Send request
                            try
                            {
                                await stream.WriteAsync(requestBytes, 0, requestBytes.Length, cancellationToken);
                                await stream.FlushAsync(cancellationToken);
                            }
                            catch (ObjectDisposedException)
                            {
                                Console.WriteLine($"[CLIENT {clientId}] Stream disposed");
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (IOException ex)
                            {
                                Console.WriteLine($"[CLIENT {clientId}] Send error: {ex.Message}");
                                errorCount++;
                                if (errorCount > 5) break;
                                continue;
                            }

                            // Read response with log ID
                            byte[] buffer = new byte[8192];
                            int bytesRead = 0;

                            try
                            {
                                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                            }
                            catch (ObjectDisposedException)
                            {
                                Console.WriteLine($"[CLIENT {clientId}] Stream disposed during read");
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (IOException ex)
                            {
                                Console.WriteLine($"[CLIENT {clientId}] Receive error: {ex.Message}");
                                errorCount++;
                                if (errorCount > 5) break;
                                continue;
                            }

                            sw.Stop();

                            if (bytesRead == 0)
                            {
                                Console.WriteLine($"[CLIENT {clientId}] Server closed connection");
                                break;
                            }

                            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            bool success = response.Contains("status>SUCCESS");

                            // Extract log ID from response
                            string logId = ExtractLogId(response);

                            Console.WriteLine($"[CLIENT {clientId}] Response #{requestCount+1}: LogID={logId}, Time={sw.ElapsedMilliseconds}ms, Success={success}");

                            RecordMetric(new TestMetrics
                            {
                                ClientId = clientId,
                                ResponseTimeMs = sw.ElapsedMilliseconds,
                                Success = success,
                                RequestSize = requestBytes.Length,
                                ResponseSize = bytesRead,
                                LogId = logId,
                                Timestamp = DateTime.UtcNow
                            });

                            requestCount++;

                            // Delay between requests
                            try
                            {
                                await Task.Delay(100, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            errorCount++;
                            if (errorCount > 5)
                            {
                                Console.WriteLine($"[CLIENT {clientId}] Too many errors, disconnecting");
                                break;
                            }
                        }
                    }
                }

                Console.WriteLine($"[CLIENT {clientId}] Completed: {requestCount} requests, {errorCount} errors");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine($"[CLIENT {clientId}] Object disposed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLIENT {clientId}] Fatal error: {ex.GetType().Name}");
            }
            finally
            {
                try
                {
                    if (client != null)
                    {
                        try
                        {
                            client.Close();
                        }
                        catch { }

                        client.Dispose();
                    }
                }
                catch { }
            }
        }

        private string ExtractLogId(string response)
        {
            try
            {
                int start = response.IndexOf("<log_id>") + 8;
                int end = response.IndexOf("</log_id>");
                if (start > 7 && end > start)
                {
                    return response.Substring(start, end - start);
                }
            }
            catch { }

            return "N/A";
        }

        private void RecordMetric(TestMetrics metric)
        {
            lock (_metricsLock)
            {
                _metrics.Add(metric);
            }
        }

        private async Task ReportStatusAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(5000, cancellationToken);

                    lock (_metricsLock)
                    {
                        int totalRequests = _metrics.Count;
                        int successCount = _metrics.Count(m => m.Success);
                        int errorCount = totalRequests - successCount;
                        double avgResponseTime = _metrics.Where(m => m.ResponseTimeMs > 0)
                            .Average(m => m.ResponseTimeMs);

                        Console.WriteLine($"[STATUS] Requests: {totalRequests} | Success: {successCount} | " +
                            $"Errors: {errorCount} | Avg: {avgResponseTime:F2}ms");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        private async Task PrintResults(Stopwatch totalTime)
        {
            await Task.Delay(1000);

            lock (_metricsLock)
            {
                int totalRequests = _metrics.Count;
                int successCount = _metrics.Count(m => m.Success);
                int errorCount = totalRequests - successCount;
                var responseTimes = _metrics.Where(m => m.ResponseTimeMs > 0).Select(m => m.ResponseTimeMs).ToList();

                double avgResponseTime = responseTimes.Any() ? responseTimes.Average() : 0;
                double minResponseTime = responseTimes.Any() ? responseTimes.Min() : 0;
                double maxResponseTime = responseTimes.Any() ? responseTimes.Max() : 0;
                double p95ResponseTime = GetPercentile(responseTimes, 95);
                double p99ResponseTime = GetPercentile(responseTimes, 99);

                double requestsPerSecond = totalRequests > 0 ? totalRequests / totalTime.Elapsed.TotalSeconds : 0;
                double successRate = totalRequests > 0 ? (successCount * 100.0) / totalRequests : 0;

                Console.WriteLine("\n╔════════════════════════════════════════════════════════╗");
                Console.WriteLine("║           LOAD TEST RESULTS                            ║");
                Console.WriteLine("╠════════════════════════════════════════════════════════╣");
                Console.WriteLine($"║ Total Duration (s):      {totalTime.Elapsed.TotalSeconds,-41:F2} ║");
                Console.WriteLine($"║ Total Requests:          {totalRequests,-41} ║");
                Console.WriteLine($"║ Successful Requests:     {successCount,-41} ║");
                Console.WriteLine($"║ Failed Requests:         {errorCount,-41} ║");
                Console.WriteLine($"║ Success Rate (%):        {successRate,-41:F2} ║");
                Console.WriteLine($"║ Requests per Second:     {requestsPerSecond,-41:F2} ║");
                Console.WriteLine("╠════════════════════════════════════════════════════════╣");
                Console.WriteLine($"║ Avg Response Time (ms):  {avgResponseTime,-41:F2} ║");
                Console.WriteLine($"║ Min Response Time (ms):  {minResponseTime,-41:F2} ║");
                Console.WriteLine($"║ Max Response Time (ms):  {maxResponseTime,-41:F2} ║");
                Console.WriteLine($"║ P95 Response Time (ms):  {p95ResponseTime,-41:F2} ║");
                Console.WriteLine($"║ P99 Response Time (ms):  {p99ResponseTime,-41:F2} ║");
                Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");

                if (successRate < 95)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"⚠️  Warning: Success rate below 95% ({successRate:F2}%)");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✅ Test successful with {successRate:F2}% success rate");
                    Console.ResetColor();
                }

                // Show sample log IDs
                Console.WriteLine("\n📋 Sample Log IDs from responses:");
                var sampleLogIds = _metrics.Where(m => m.LogId != "N/A").Take(5).Select(m => m.LogId).Distinct();
                foreach (var logId in sampleLogIds)
                {
                    Console.WriteLine($"  - {logId}");
                }
            }
        }

        private double GetPercentile(List<long> values, int percentile)
        {
            if (values.Count == 0) return 0;

            var sorted = values.OrderBy(x => x).ToList();
            int index = (int)Math.Ceiling((percentile / 100.0) * sorted.Count) - 1;

            return sorted[Math.Max(0, index)];
        }
    }

    public class TestMetrics
    {
        public int ClientId { get; set; }
        public long ResponseTimeMs { get; set; }
        public bool Success { get; set; }
        public int RequestSize { get; set; }
        public int ResponseSize { get; set; }
        public string LogId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}