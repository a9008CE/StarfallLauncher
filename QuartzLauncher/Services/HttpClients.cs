using System.Net;
using System.Net.Http;
using System.Security.Authentication;

namespace QuartzLauncher.Services;

public static class HttpClients
{
    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            MaxConnectionsPerServer = 256,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        })
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };
        return client;
    }
}
