using System.Net;
using System.Net.Mail;
using FinanceERP.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FinanceERP.Infrastructure.Services;

/// <summary>
/// Optional SMTP delivery for notifications. Disabled unless Smtp:Host is
/// configured; failures are logged and never break the calling workflow.
/// Config keys: Smtp:Host, Smtp:Port (587), Smtp:User, Smtp:Password,
/// Smtp:From, Smtp:EnableSsl (true).
/// </summary>
public class EmailService(IConfiguration config, ILogger<EmailService> logger) : IAppEmailSender
{
    public bool Enabled => !string.IsNullOrWhiteSpace(config["Smtp:Host"]);

    public async Task SendAsync(string toEmail, string subject, string body)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(toEmail)) return;
        try
        {
            using var client = new SmtpClient(config["Smtp:Host"], config.GetValue("Smtp:Port", 587))
            {
                EnableSsl = config.GetValue("Smtp:EnableSsl", true),
                Credentials = string.IsNullOrEmpty(config["Smtp:User"])
                    ? null
                    : new NetworkCredential(config["Smtp:User"], config["Smtp:Password"])
            };
            var from = config["Smtp:From"] ?? config["Smtp:User"] ?? "finance-erp@localhost";
            using var message = new MailMessage(from, toEmail, $"[Finance ERP] {subject}", body);
            await client.SendMailAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email to {To} failed ({Subject})", toEmail, subject);
        }
    }
}
