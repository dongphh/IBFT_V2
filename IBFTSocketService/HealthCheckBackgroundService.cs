using IBFTSocketService.Monitoring;


namespace IBFTSocketService
{
     /// <summary>
    /// Health check background service
    /// Performs periodic database connection tests
    /// </summary>
    public class HealthCheckBackgroundService: BackgroundService
    {
        private readonly HealthCheckService _healthCheck;
        private readonly ILogger<HealthCheckBackgroundService> _logger;
        private Timer _healthCheckTimer;

        public HealthCheckBackgroundService(HealthCheckService healthCheck, ILogger<HealthCheckBackgroundService> logger)
        {
            _healthCheck = healthCheck;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _healthCheckTimer = new Timer(PerformHealthCheck, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        }

        private void PerformHealthCheck(object state)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    var result = await _healthCheck.CheckAsync();
                    if (!result.IsHealthy)
                    {
                        // Log but don't spam
                    }
                }, default);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _healthCheckTimer?.Dispose();
            return base.StopAsync(cancellationToken);
        }
    }
}
