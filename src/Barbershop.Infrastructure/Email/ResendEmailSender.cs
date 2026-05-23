using System.Net.Http.Json;
using Barbershop.Application.Email;
using Barbershop.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Email;

public sealed class ResendEmailSender : IEmailSender
{
    private readonly EmailOptions _emailOptions;
    private readonly ResendOptions _resendOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(
        IOptions<EmailOptions> emailOptions,
        IOptions<ResendOptions> resendOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<ResendEmailSender> logger)
    {
        _emailOptions = emailOptions.Value;
        _resendOptions = resendOptions.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!_emailOptions.Enabled)
        {
            _logger.LogInformation("Email delivery skipped (disabled). To={To} Subject={Subject}", message.To, message.Subject);
            return;
        }

        if (_resendOptions.SandboxMode)
        {
            _logger.LogInformation("Sandbox mode: email not sent. To={To} Subject={Subject}", message.To, message.Subject);
            return;
        }

        var from = message.From ?? $"{_emailOptions.DefaultFromName} <{_emailOptions.DefaultFromAddress}>";

        var payload = new
        {
            from,
            to = new[] { message.To },
            subject = message.Subject,
            html = message.HtmlBody,
            text = message.TextBody,
        };

        var client = _httpClientFactory.CreateClient("Resend");
        var response = await client.PostAsJsonAsync("emails", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Resend API error {StatusCode} for {To}: {Body}", (int)response.StatusCode, message.To, body);
            throw new InvalidOperationException($"Email delivery failed with status {(int)response.StatusCode}.");
        }

        _logger.LogInformation("Email sent via Resend. To={To} Subject={Subject}", message.To, message.Subject);
    }
}
