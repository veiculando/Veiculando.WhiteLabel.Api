using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Veiculando.WhiteLabel.Api.Services;

public interface IWlAppEmailSender
{
    Task ConfirmacaoAsync(string email, string marca, string codigo, CancellationToken ct);
    Task RecuperacaoAsync(string email, string marca, string link, CancellationToken ct);
}

public sealed class SendGridWlAppEmailSender : IWlAppEmailSender
{
    private readonly WlPasswordEmailOptions _options;
    public SendGridWlAppEmailSender(IOptions<WlPasswordEmailOptions> options) => _options = options.Value;

    public Task ConfirmacaoAsync(string email, string marca, string codigo, CancellationToken ct) =>
        EnviarAsync(email, $"Confirme seu e-mail — {marca}",
            $"Seu código de confirmação em {marca} é {codigo}. Ele expira em 15 minutos e só pode ser usado uma vez. Se não solicitou este cadastro, ignore este e-mail.", ct);

    public Task RecuperacaoAsync(string email, string marca, string link, CancellationToken ct) =>
        EnviarAsync(email, $"Recuperação de senha — {marca}",
            $"Para redefinir sua senha em {marca}, acesse {link}. O link expira em 30 minutos e só pode ser usado uma vez. Se não solicitou a alteração, ignore este e-mail.", ct);

    private async Task EnviarAsync(string email, string assunto, string texto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SendGridApiKey) || string.IsNullOrWhiteSpace(_options.FromEmail))
            throw new WlPasswordEmailException("Remetente de e-mail do App não configurado.");
        var message = MailHelper.CreateSingleEmail(new EmailAddress(_options.FromEmail, _options.FromName),
            new EmailAddress(email), assunto, texto, $"<p>{WebUtility.HtmlEncode(texto)}</p>");
        var result = await new SendGridClient(_options.SendGridApiKey).SendEmailAsync(message, ct);
        if (!result.IsSuccessStatusCode) throw new WlPasswordEmailException("Provedor recusou o envio do e-mail do App.");
    }
}
