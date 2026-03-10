using System.Net;
using System.Net.Mail;

namespace OpenTrafficDB.Services;

public class SmtpEmailService : IEmailService
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _pass;
    private readonly bool _ssl;
    private readonly string _from;
    private readonly string _fromName;
    private readonly ILogger<SmtpEmailService> logger;

    public SmtpEmailService(IConfiguration config, ILogger<SmtpEmailService> logger)
    {
        _host = config["Email:SmtpHost"] ?? string.Empty;
        _port = int.TryParse(config["Email:SmtpPort"], out var p) ? p : 587;
        _user = config["Email:SmtpUser"] ?? string.Empty;
        _pass = config["Email:SmtpPassword"] ?? string.Empty;
        _from = config["Email:FromAddress"] ?? "noreply@opentrafficdb.org";
        _fromName = config["Email:FromName"] ?? "OpenTrafficDB";
        bool.TryParse(config["Email:EnableSsl"] ?? "true", out _ssl);
        this.logger = logger;
    } 

    public Task SendVerificationEmailAsync(string toEmail, string verificationUrl)
    {
        var subject = "Verify your OpenTrafficDB email address";
        var body = $"""
            <p>Welcome to <strong>OpenTrafficDB</strong>!</p>
            <p>Click the link below to verify your email address and activate your account:</p>
            <p><a href="{verificationUrl}" style="font-size:16px">{verificationUrl}</a></p>
            <p>This link expires in <strong>24 hours</strong>.</p>
            <p style="color:#888;font-size:12px">If you did not register, you can safely ignore this email.</p>
            """;
        return SendAsync(toEmail, subject, body, "Verification");
    }

    public Task SendPasswordResetEmailAsync(string toEmail, string resetUrl)
    {
        var subject = "Reset your OpenTrafficDB password";
        var body = $"""
            <p>You requested a password reset for your <strong>OpenTrafficDB</strong> account.</p>
            <p>Click the link below to set a new password:</p>
            <p><a href="{resetUrl}" style="font-size:16px">{resetUrl}</a></p>
            <p>This link expires in <strong>1 hour</strong>.</p>
            <p style="color:#888;font-size:12px">If you did not request a password reset, you can safely ignore this email.</p>
            """;
        return SendAsync(toEmail, subject, body, "PasswordReset");
    }

    public Task SendEmailChangeVerificationAsync(string toEmail, string verifyUrl)
    {
        var subject = "Confirm your new OpenTrafficDB email address";
        var body = $"""
            <p>You requested an email address change for your <strong>OpenTrafficDB</strong> account.</p>
            <p>Click the link below to confirm your new email address:</p>
            <p><a href="{verifyUrl}" style="font-size:16px">{verifyUrl}</a></p>
            <p>This link expires in <strong>24 hours</strong>.</p>
            <p style="color:#888;font-size:12px">If you did not request this change, you can safely ignore this email.</p>
            """;
        return SendAsync(toEmail, subject, body, "EmailChange");
    }

    private async Task SendAsync(string toEmail, string subject, string body, string logLabel)
    {
        if (string.IsNullOrEmpty(_host))
        {
            logger.LogWarning(
                "Email not configured (Email:SmtpHost missing). {Label} email for {Email}: {Body}",
                logLabel, toEmail, body);
            return;
        }

        using var message = new MailMessage();
        message.From = new MailAddress(_from, _fromName);
        message.To.Add(toEmail);
        message.Subject = subject;
        message.Body = body;
        message.IsBodyHtml = true;

        using var client = new SmtpClient(_host, _port);
        client.DeliveryMethod = SmtpDeliveryMethod.Network;
        client.UseDefaultCredentials = false;
        client.EnableSsl = _ssl;
        client.Credentials = new NetworkCredential(_user, _pass);

        try
        {
            await client.SendMailAsync(message);
            logger.LogInformation("{Label} email sent to {Email}", logLabel, toEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send {Label} email to {Email}", logLabel, toEmail);
            throw;
        }
    }
}
