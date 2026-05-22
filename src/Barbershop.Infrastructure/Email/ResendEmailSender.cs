using Barbershop.Application.Email;
using Barbershop.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Email;

public sealed class ResendEmailSender : IEmailSender
{
    private readonly EmailOptions _emailOptions;
    private readonly ResendOptions _resendOptions;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(
        IOptions<EmailOptions> emailOptions,
        IOptions<ResendOptions> resendOptions,
        ILogger<ResendEmailSender> logger)
    {
        _emailOptions = emailOptions.Value;
        _resendOptions = resendOptions.Value;
        _logger = logger;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!_emailOptions.Enabled)
        {
            _logger.LogInformation("Email delivery skipped because email is disabled.");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Email delivery placeholder invoked for {Recipient} using provider {Provider}. Sandbox={SandboxMode} ConfiguredApiKey={ConfiguredApiKey}",
            message.To,
            _emailOptions.Provider,
            _resendOptions.SandboxMode,
            OptionsValidationHelpers.IsConfigured(_resendOptions.ApiKey));

        return Task.CompletedTask;
    }
}
