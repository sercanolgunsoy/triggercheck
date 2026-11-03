namespace TriggerCheck.Domain.Entities;

public enum MonitorType
{
    Http = 0,      // HTTP(S) request, success = expected status code
    Keyword = 1,   // HTTP(S) request + response body must contain Keyword
    Tcp = 2,       // TCP connect to Host:Port
    Ping = 3       // ICMP ping to Host
}

public enum MonitorStatus
{
    Unknown = 0,   // never checked yet
    Up = 1,
    Down = 2,
    Paused = 3
}

public enum AlertChannelType
{
    Email = 0,
    Webhook = 1
}
