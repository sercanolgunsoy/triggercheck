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
    Webhook = 1,     // generic JSON POST
    Slack = 2,       // Slack incoming webhook URL
    Teams = 3,       // Microsoft Teams Workflows ("When a Teams webhook request is received") URL
    Telegram = 4,    // Target = chat id, AlertChannel.Secret = bot token
    Sms = 5,         // Twilio SMS, Target = E.164 phone number (plan must allow SMS/voice)
    VoiceCall = 6    // Twilio voice call, Target = E.164 phone number (plan must allow SMS/voice)
}
