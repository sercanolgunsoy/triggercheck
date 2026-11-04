using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Web.StatusPages;

/// <summary>
/// Adds e-mail outbox rows to the caller's context, so they are saved in the same transaction as the idempotency claim
/// of a background job (status page events, monthly reports): either both are stored or neither, so a crash between
/// claiming and queuing can neither lose nor duplicate e-mails. Same truncation as <c>OutboxAppEmailSender</c>; the
/// caller saves and then wakes the sender (<c>AppEmailSignal.Notify</c>).
/// </summary>
public static class OutboxRows
{
    public static void Add(AppDbContext db, string to, string subject, string body, DateTime nowUtc) =>
        db.EmailOutbox.Add(new EmailOutbox
        {
            To = to.Length > 256 ? to[..256] : to,
            Subject = subject.Length > 300 ? subject[..300] : subject,
            Body = body,
            CreatedAtUtc = nowUtc
        });
}
