using IBFTSocketService.Monitoring;

namespace IBFTSocketService
{
    /// <summary>
    /// Monitoring background service
    /// Tracks performance metrics and database health
    /// </summary>
    public class MonitoringService : BackgroundService
    {
        private readonly PerformanceMonitor _monitor;
        private readonly ILogger<MonitoringService> _logger;
        private Timer _monitoringTimer;

        public MonitoringService(PerformanceMonitor monitor, ILogger<MonitoringService> logger)
        {
            _monitor = monitor;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _monitoringTimer = new Timer(MonitorHealth, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        }

        private void MonitorHealth(object state)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    await _monitor.CheckDatabaseHealthAsync();
                }, default);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health monitoring");
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _monitoringTimer?.Dispose();
            return base.StopAsync(cancellationToken);
        }
    }
}
