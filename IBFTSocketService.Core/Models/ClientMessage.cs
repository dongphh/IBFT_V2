
namespace IBFTSocketService.Core.Models
{
    public class ClientMessage
    {
        public required string CommandType { get; set; }
        public required string Payload { get; set; }
        public Dictionary<string, object>? Parameters { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static ClientMessage FromString(string data)
        {
            try
            {
                var parts = data.Split('|', 3);
                if (parts.Length < 2)
                    throw new InvalidOperationException("Invalid message format");

                return new ClientMessage
                {
                    CommandType = parts[0].Trim(),
                    Payload = parts[1].Trim(),
                    Parameters = parts.Length > 2 ? ParseParameters(parts[2]) : new Dictionary<string, object>()
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error parsing message: {ex.Message}", ex);
            }
        }

        private static Dictionary<string, object> ParseParameters(string paramString)
        {
            var dict = new Dictionary<string, object>();
            try
            {
                if (string.IsNullOrEmpty(paramString))
                    return dict;

                foreach (var param in paramString.Split(';'))
                {
                    if (string.IsNullOrEmpty(param)) continue;

                    var kv = param.Split('=');
                    if (kv.Length == 2)
                        dict[kv[0].Trim()] = kv[1].Trim();
                }
            }
            catch { }

            return dict;
        }
    }
}
