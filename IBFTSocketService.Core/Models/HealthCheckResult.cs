namespace IBFTSocketService.Core.Models
{
    public class HealthCheckResult
    {
        public bool IsHealthy { get; set; }
        public required string Status { get; set; }
        public Dictionary<string, object> Details { get; set; } = new();
        public DateTime CheckTime { get; set; } = DateTime.UtcNow;

        public override string ToString()
        {
            var details = string.Join(", ", Details.Select(x => $"{x.Key}={x.Value}"));
            return $"[{Status}] {details}";
        }
    }
}
