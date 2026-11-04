using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using TriggerCheck.Monitoring.Security;

namespace TriggerCheck.Monitoring.Checks;

/// <summary>Turns probe exceptions into short, human readable error strings.</summary>
public static class ProbeErrors
{
    public const int MaxLength = 500;

    public static string Describe(Exception ex, TimeSpan timeout)
    {
        if (Find<BlockedTargetException>(ex) is { } blocked) return Truncate(blocked.Message)!;
        var text = ex switch
        {
            OperationCanceledException or TimeoutException => $"Timeout after {timeout.TotalSeconds:0.#}s",
            HttpRequestException hre => DescribeHttp(hre),
            SocketException se => DescribeSocket(se),
            PingException pe => "Ping failed: " + Innermost(pe).Message,
            AuthenticationException ae => "TLS/SSL error: " + ae.Message,
            _ => Innermost(ex).Message
        };
        return Truncate(text)!;
    }

    private static string DescribeHttp(HttpRequestException ex)
    {
        var socket = Find<SocketException>(ex);
        switch (ex.HttpRequestError)
        {
            case HttpRequestError.NameResolutionError:
                return "DNS resolution failed: " + (socket?.Message ?? ex.Message);
            case HttpRequestError.SecureConnectionError:
                return "TLS/SSL error: " + (Find<AuthenticationException>(ex)?.Message ?? Innermost(ex).Message);
            case HttpRequestError.ConnectionError:
                return socket is not null ? DescribeSocket(socket) : "Connection failed: " + Innermost(ex).Message;
        }
        if (socket is not null) return DescribeSocket(socket);
        if (Find<AuthenticationException>(ex) is { } auth) return "TLS/SSL error: " + auth.Message;
        return "HTTP error: " + Innermost(ex).Message;
    }

    private static string DescribeSocket(SocketException se) => se.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "DNS resolution failed: " + se.Message,
        SocketError.ConnectionRefused => "Connection refused",
        SocketError.TimedOut => "Connection timed out",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown => "Host unreachable",
        SocketError.ConnectionReset => "Connection reset by peer",
        _ => "Connection failed: " + se.Message
    };

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is T t) return t;
        return null;
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    public static string? Truncate(string? s) =>
        s is null || s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
}
