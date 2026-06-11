using System;
using System.Net.Http;
using System.Security.Authentication;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Cria HttpClient com TLS 1.2/1.3 habilitado explicitamente. No Windows 7 o
    /// .NET não negocia TLS 1.2 por padrão, então downloads e checagem de update
    /// (GitHub, AMD — todos HTTPS) falhariam em silêncio. Forçar Tls12|Tls13
    /// resolve no Win7 (negocia 1.2) sem perder o 1.3 no Win10/11.
    /// </summary>
    public static class HttpFactory
    {
        public static HttpClient Create(TimeSpan timeout, string userAgent)
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions =
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }
            };

            var http = new HttpClient(handler) { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            return http;
        }
    }
}
