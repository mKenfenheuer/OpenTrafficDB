namespace OpenTrafficDB.App.Services;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string toEmail, string verificationUrl);
    Task SendPasswordResetEmailAsync(string toEmail, string resetUrl);
    Task SendEmailChangeVerificationAsync(string toEmail, string verifyUrl);
}
