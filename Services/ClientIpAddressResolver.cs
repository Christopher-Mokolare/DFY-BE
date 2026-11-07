using System.Net;

namespace DoForYou.API.Services;

public static class ClientIpAddressResolver
{
    public static string GetRateLimitPartitionKey(HttpContext context, bool trustManagedIngress)
    {
        if (trustManagedIngress)
        {
            // Cloudflare overwrites CF-Connecting-IP at its edge and sends a single
            // address to Render. Reject comma-separated or malformed values rather
            // than trusting a caller-controlled forwarded-IP chain.
            var cloudflareClientIp = context.Request.Headers["CF-Connecting-IP"].ToString();
            if (IPAddress.TryParse(cloudflareClientIp, out var parsedClientIp))
                return parsedClientIp.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
