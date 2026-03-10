namespace OpenTrafficDB.Services;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string toEmail, string verificationUrl);
}
