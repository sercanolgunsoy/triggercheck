namespace TriggerCheck.Domain.Entities;

public enum MonitorType
{
    Http = 0,      // HTTP(S) request, success = expected status code
    Keyword = 1,   // HTTP(S) request + response body must contain Keyword
    Tcp = 2,       // TCP connect to Host:Port
    Ping = 3,      // ICMP ping to Host
    Dns = 4,       // DNS query of DnsRecordType for Target (host name), optional expected value
    Heartbeat = 5, // push: the monitored job calls /hb/{token}; DOWN when no ping within interval + grace
    JsonApi = 6    // HTTP(S) request + JSON path assertion on the response body
}

/// <summary>DNS record types a Dns monitor can query. Values are the RFC 1035 / 3596 type codes.</summary>
public enum DnsRecordType
{
    A = 1,
    NS = 2,
    CNAME = 5,
    MX = 15,
    TXT = 16,
    AAAA = 28
}

/// <summary>Assertion of a JsonApi monitor (all five) or a Dns monitor (Equal / Contains only).</summary>
public enum AssertionOperator
{
    Equal = 0,
    Contains = 1,
    Exists = 2,
    LessThan = 3,
    GreaterThan = 4
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
    Webhook = 1,     // generic JSON POST
    Slack = 2,       // Slack incoming webhook URL
    Teams = 3,       // Microsoft Teams Workflows ("When a Teams webhook request is received") URL
    Telegram = 4,    // Target = chat id, AlertChannel.Secret = bot token
    Sms = 5,         // Twilio SMS, Target = E.164 phone number (plan must allow SMS/voice)
    VoiceCall = 6    // Twilio voice call, Target = E.164 phone number (plan must allow SMS/voice)
}
