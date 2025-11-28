using IBFTSocketService;
using IBFTSocketService.Core.Configuration;
using IBFTSocketService.Data.Repository;
using IBFTSocketService.Monitoring;
using Serilog;

namespace SocketService
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Setup Serilog logging - Unified single file with 200MB rolling
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File("logs/IBFT_V2_Service.log",
                    rollingInterval: RollingInterval.Infinite,
                    fileSizeLimitBytes: 200 * 1024 * 1024, // 200MB
                    retainedFileCountLimit: 10,
                    rollOnFileSizeLimit: true,
                    encoding: System.Text.Encoding.UTF8,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .Enrich.FromLogContext()
                .CreateLogger();

            try
            {
                Log.Information("════════════════════════════════════════════════════");
                Log.Information("🚀 Socket Service Starting");
                Log.Information("Environment: {Environment}",
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production");
                Log.Information("════════════════════════════════════════════════════");

                var host = CreateHostBuilder(args).Build();

                // Graceful shutdown handling
                using (var cts = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (s, e) =>
                    {
                        e.Cancel = true;
                        Log.Warning("⚠️  Shutdown signal received (Ctrl+C)");
                        cts.Cancel();
                    };

                    AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                    {
                        Log.Warning("⚠️  Process exit signal received");
                        cts.Cancel();
                    };

                    try
                    {
                        await host.RunAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Information("🛑 Host cancellation requested");
                    }
                    finally
                    {
                        cts.Dispose();
                    }
                }

                Log.Information("🛑 Socket Service stopped");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "❌ Application terminated unexpectedly");
            }
            finally
            {
                Log.Information("════════════════════════════════════════════════════");
                Log.Information("Cleanup complete");
                Log.CloseAndFlush();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config
                        .SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                        .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
                            optional: true, reloadOnChange: true)
                        .AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) =>
                {
                    // Load configuration
                    var socketConfig = new SocketServerConfig();
                    var oracleConfig = new OraclePoolConfig();

                    context.Configuration.GetSection("SocketServer").Bind(socketConfig);
                    context.Configuration.GetSection("OraclePool").Bind(oracleConfig);

                    // Register configurations
                    services.AddSingleton(socketConfig);
                    services.AddSingleton(oracleConfig);

                    // Register Oracle pool manager - IMPORTANT: Must initialize before use
                    services.AddSingleton<OracleConnectionPoolManager>(provider =>
                    {
                        var logger = provider.GetRequiredService<ILogger<OracleConnectionPoolManager>>();
                        var config = provider.GetRequiredService<IConfiguration>();

                        var connectionString = config.GetConnectionString("OracleConnection");

                        if (string.IsNullOrEmpty(connectionString))
                        {
                            throw new InvalidOperationException(
                                "Oracle connection string not found in appsettings.json under ConnectionStrings:OracleConnection");
                        }

                        var poolManager = OracleConnectionPoolManager.Instance;
                        poolManager.Initialize(connectionString, oracleConfig, logger);

                        logger.LogInformation("✓ Oracle Connection Pool Manager initialized successfully");
                        return poolManager;
                    });

                    // Register data access
                    services.AddScoped<IDataRepository, DataRepository>();

                    // Register monitoring
                    services.AddSingleton<PerformanceMonitor>();
                    services.AddSingleton<HealthCheckService>();

                    // Register ILoggerFactory for creating typed loggers
                    services.AddSingleton<ILoggerFactory>(sp =>
                    {
                        return LoggerFactory.Create(builder =>
                        {
                            builder.AddSerilog();
                        });
                    });

                    // Register hosted services
                    services.AddHostedService<IBFT_V2_Service>();
                    services.AddSingleton<RemoteService>();
                    services.AddHostedService<RemoteService>();
                    services.AddHostedService<MonitoringService>();
                    services.AddHostedService<HealthCheckBackgroundService>();
                })
                .UseWindowsService();
    }
}