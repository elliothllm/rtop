using System.Net.Security;
using System.Net.Sockets;

namespace Rtop.Core;

/// <summary>
/// Works out whether a local port speaks TLS, so that opening it in a browser picks the right
/// scheme. A dev server commonly listens on both an https and an http port with no way to tell
/// them apart from the number alone, so ask the socket rather than guessing.
/// </summary>
public static class UrlProbe
{
    private static readonly Dictionary<int, string> Cache = [];

    public static async Task<string> UrlForAsync(int port, CancellationToken cancellationToken = default)
    {
        if (Cache.TryGetValue(port, out var cached))
        {
            return cached;
        }

        var scheme = await IsTlsAsync(port, cancellationToken) ? "https" : "http";
        var url = $"{scheme}://localhost:{port}";
        Cache[port] = url;
        return url;
    }

    private static async Task<bool> IsTlsAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);

            await using var ssl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            // A dev certificate is almost never trusted, hence the permissive callback: the
            // question is only whether the other end speaks TLS at all.
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            }, timeout.Token);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested is false)
        {
            return false;
        }
    }
}
