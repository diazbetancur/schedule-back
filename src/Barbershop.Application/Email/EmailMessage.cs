namespace Barbershop.Application.Email;

public sealed record EmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string? TextBody = null,
    string? From = null);
