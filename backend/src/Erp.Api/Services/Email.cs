using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Erp.Api.Services;

// ------------------------------------------------------------------------------------------------
// Outgoing email (security alerts). Messages go through an in-process queue and are sent by a
// background worker, so a slow or failing mail server never delays or breaks the request that
// triggered the email. Without SMTP settings the worker only logs what it would have sent.
// ------------------------------------------------------------------------------------------------

/// <summary>
/// SMTP settings. Read from flat env vars (SMTP_HOST, SMTP_PORT, SMTP_USERNAME, SMTP_PASSWORD, EMAIL_FROM,
/// EMAIL_FROM_NAME) like JWT_SECRET, or from the "Email" config section (e.g. dotnet user-secrets in dev).
/// </summary>
public sealed class EmailOptions
{
    public string? Host { get; set; }
    /// <summary>587 = STARTTLS (Gmail default), 465 = TLS from the start.</summary>
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    /// <summary>Sender address. Defaults to Username (Gmail only sends as the signed-in account).</summary>
    public string? From { get; set; }
    public string FromName { get; set; } = "ERP Platform";

    /// <summary>Host and sender set, and a password whenever a username is (a half-filled Gmail setup only logs).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress)
        && (string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password));
    public string? FromAddress => string.IsNullOrWhiteSpace(From) ? Username : From;

    public static EmailOptions FromConfiguration(IConfiguration cfg)
    {
        var o = new EmailOptions();
        cfg.GetSection("Email").Bind(o);
        o.Host = cfg["SMTP_HOST"] ?? o.Host;
        if (int.TryParse(cfg["SMTP_PORT"], out var port)) o.Port = port;
        o.Username = cfg["SMTP_USERNAME"] ?? o.Username;
        o.Password = cfg["SMTP_PASSWORD"] ?? o.Password;
        o.From = cfg["EMAIL_FROM"] ?? o.From;
        o.FromName = cfg["EMAIL_FROM_NAME"] ?? o.FromName;
        return o;
    }
}

public sealed record EmailMessage(string To, string ToName, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        var o = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(new MailboxAddress(m.ToName, m.To));
        mime.Subject = m.Subject;
        mime.Body = new BodyBuilder { TextBody = m.TextBody, HtmlBody = m.HtmlBody }.ToMessageBody();

        using var smtp = new SmtpClient { Timeout = 20_000 };
        await smtp.ConnectAsync(o.Host, o.Port, o.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        if (!string.IsNullOrEmpty(o.Username)) await smtp.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        await smtp.SendAsync(mime, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

/// <summary>Used when SMTP isn't configured (local dev without credentials, tests): logs instead of sending.</summary>
public sealed class LogOnlyEmailSender(ILogger<LogOnlyEmailSender> log) : IEmailSender
{
    public Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        log.LogInformation("Email not sent (SMTP not configured): to {To}, subject {Subject}", m.To, m.Subject);
        return Task.CompletedTask;
    }
}

public interface IEmailQueue
{
    /// <summary>Never blocks: if the queue is full the message is dropped and logged.</summary>
    void Enqueue(EmailMessage message);
}

/// <summary>Bounded in-process queue + background worker that sends with one retry.</summary>
public sealed class EmailDispatcher(IEmailSender sender, ILogger<EmailDispatcher> log) : BackgroundService, IEmailQueue
{
    private readonly Channel<EmailMessage> _queue = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Enqueue(EmailMessage message)
    {
        if (!_queue.Writer.TryWrite(message))
            log.LogWarning("Email queue full; dropped email to {To} ({Subject})", message.To, message.Subject);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var m in _queue.Reader.ReadAllAsync(stop))
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await sender.SendAsync(m, stop);
                    break;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) when (attempt == 1)
                {
                    log.LogWarning(ex, "Sending email to {To} failed; retrying once", m.To);
                    await Task.Delay(TimeSpan.FromSeconds(5), stop);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Could not send email to {To} ({Subject})", m.To, m.Subject);
                }
            }
        }
    }
}
