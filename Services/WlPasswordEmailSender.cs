using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Configuração do remetente de e-mails transacionais de recuperação de senha.
    /// </summary>
    /// <remarks>
    /// API key e remetente NÃO são versionados: em produção vêm de Key
    /// Vault/variáveis de ambiente (ADR-WL-005), via
    /// <c>WlPasswordEmail__SendGridApiKey</c> e <c>WlPasswordEmail__FromEmail</c>.
    /// A identificação de marca no corpo do e-mail vem de <c>WL_Configuracao</c>
    /// do tenant (ver <see cref="IWlPasswordEmailSender.EnviarRecuperacaoAsync"/>) —
    /// a caixa de envio em si é única, compartilhada por todas as instâncias.
    /// </remarks>
    public sealed class WlPasswordEmailOptions
    {
        public string SendGridApiKey { get; set; }
        public string FromEmail { get; set; }
        public string FromName { get; set; } = "Veiculando WhiteLabel";
        public int ConviteValidadeHoras { get; set; } = 48;
    }

    /// <summary>
    /// Transporte de e-mail do fluxo de recuperação de senha dos operadores WL.
    /// </summary>
    /// <remarks>
    /// Deliberadamente separado do EmailService do core: aquele serve os fluxos de
    /// Usuario/Anunciante do core e não conhece marca por afiliada nem o
    /// vocabulário de operador WL. Duplicar a interface aqui é mais barato do que
    /// acoplar o BFF a um contrato que muda por motivos alheios a este fluxo.
    /// </remarks>
    public interface IWlPasswordEmailSender
    {
        Task EnviarConviteAsync(
            string destinatarioEmail,
            string nomeExibicaoMarca,
            string linkPrimeiroAcesso,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Envia o e-mail de recuperação de senha.
        /// </summary>
        /// <param name="destinatarioEmail">E-mail do operador.</param>
        /// <param name="nomeExibicaoMarca">
        /// <c>WL_Configuracao.NomeExibicao</c> do tenant — identifica a marca no
        /// template. Cai para um nome genérico quando o branding não está
        /// configurado para a afiliada.
        /// </param>
        /// <param name="linkReset">URL completa de redefinição de senha.</param>
        /// <exception cref="WlPasswordEmailException">
        /// O provedor recusou ou falhou o envio. O chamador deve invalidar o
        /// token de recuperação já gerado nesse caso.
        /// </exception>
        Task EnviarRecuperacaoAsync(
            string destinatarioEmail,
            string nomeExibicaoMarca,
            string linkReset,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Falha de envio do e-mail de recuperação de senha.
    /// </summary>
    /// <remarks>
    /// A mensagem nunca carrega o link, o token ou o e-mail do destinatário —
    /// só o fato de ter falhado. Quem loga a partir daqui não vaza PII.
    /// </remarks>
    public sealed class WlPasswordEmailException : Exception
    {
        public WlPasswordEmailException(string message) : base(message) { }
        public WlPasswordEmailException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Implementação Task-based sobre a API HTTP do SendGrid.
    /// </summary>
    /// <remarks>
    /// Sem <c>async void</c> em lugar nenhum: <see cref="EnviarRecuperacaoAsync"/>
    /// devolve <see cref="Task"/> e propaga exceção normalmente, para que o
    /// controller consiga invalidar o token num <c>catch</c>.
    /// </remarks>
    public sealed class SendGridWlPasswordEmailSender : IWlPasswordEmailSender
    {
        private readonly WlPasswordEmailOptions _options;
        private readonly ILogger<SendGridWlPasswordEmailSender> _logger;

        public SendGridWlPasswordEmailSender(
            IOptions<WlPasswordEmailOptions> options,
            ILogger<SendGridWlPasswordEmailSender> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        public async Task EnviarRecuperacaoAsync(
            string destinatarioEmail,
            string nomeExibicaoMarca,
            string linkReset,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(destinatarioEmail))
                throw new ArgumentException("Destinatário é obrigatório.", nameof(destinatarioEmail));
            if (string.IsNullOrWhiteSpace(linkReset))
                throw new ArgumentException("Link de redefinição é obrigatório.", nameof(linkReset));

            if (string.IsNullOrWhiteSpace(_options.SendGridApiKey) || string.IsNullOrWhiteSpace(_options.FromEmail))
            {
                // Falta de configuração é bug de deploy, não caso de negócio: o
                // chamador trata isso como falha de envio (invalida o token) e o
                // log real fica visível no servidor, não escondido atrás da
                // resposta 200 genérica do endpoint.
                throw new WlPasswordEmailException(
                    "Remetente de e-mail não configurado (WlPasswordEmail:SendGridApiKey / FromEmail).");
            }

            var marca = string.IsNullOrWhiteSpace(nomeExibicaoMarca) ? "Veiculando" : nomeExibicaoMarca.Trim();
            var linkEscapado = WebUtility.HtmlEncode(linkReset);

            var mensagem = MailHelper.CreateSingleEmail(
                from: new EmailAddress(_options.FromEmail, _options.FromName),
                to: new EmailAddress(destinatarioEmail),
                subject: $"Recuperação de senha — {marca}",
                plainTextContent:
                    $"Recebemos uma solicitação para redefinir sua senha em {marca}.\n\n" +
                    "Se foi você, acesse o link abaixo para criar uma nova senha. " +
                    "O link expira em 2 horas e só pode ser usado uma vez.\n\n" +
                    $"{linkReset}\n\n" +
                    "Se você não solicitou esta alteração, ignore este e-mail — sua senha continua a mesma.",
                htmlContent:
                    $"<p>Recebemos uma solicitação para redefinir sua senha em <strong>{WebUtility.HtmlEncode(marca)}</strong>.</p>" +
                    "<p>Se foi você, clique no botão abaixo para criar uma nova senha. " +
                    "O link expira em <strong>2 horas</strong> e só pode ser usado uma vez.</p>" +
                    $"<p><a href=\"{linkEscapado}\" style=\"display:inline-block;padding:10px 20px;background:#1a1a1a;color:#fff;text-decoration:none;border-radius:4px;\">Redefinir senha</a></p>" +
                    "<p>Se você não solicitou esta alteração, ignore este e-mail — sua senha continua a mesma.</p>");

            var client = new SendGridClient(_options.SendGridApiKey);

            Response resposta;
            try
            {
                resposta = await client.SendEmailAsync(mensagem, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Nunca logar destinatarioEmail nem linkReset — só o fato da falha.
                _logger.LogError(ex, "Falha de transporte ao enviar e-mail de recuperação de senha via SendGrid.");
                throw new WlPasswordEmailException("Falha ao enviar e-mail de recuperação de senha.", ex);
            }

            if (!resposta.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "SendGrid recusou o envio do e-mail de recuperação de senha. StatusCode={StatusCode}",
                    (int)resposta.StatusCode);
                throw new WlPasswordEmailException($"SendGrid recusou o envio (status {(int)resposta.StatusCode}).");
            }
        }

        public async Task EnviarConviteAsync(
            string destinatarioEmail,
            string nomeExibicaoMarca,
            string linkPrimeiroAcesso,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(destinatarioEmail))
                throw new ArgumentException("Destinatário é obrigatório.", nameof(destinatarioEmail));
            if (string.IsNullOrWhiteSpace(linkPrimeiroAcesso))
                throw new ArgumentException("Link de primeiro acesso é obrigatório.", nameof(linkPrimeiroAcesso));
            if (string.IsNullOrWhiteSpace(_options.SendGridApiKey) || string.IsNullOrWhiteSpace(_options.FromEmail))
                throw new WlPasswordEmailException(
                    "Remetente de e-mail não configurado (WlPasswordEmail:SendGridApiKey / FromEmail).");

            var marca = string.IsNullOrWhiteSpace(nomeExibicaoMarca) ? "Veiculando" : nomeExibicaoMarca.Trim();
            var linkEscapado = WebUtility.HtmlEncode(linkPrimeiroAcesso);
            var mensagem = MailHelper.CreateSingleEmail(
                from: new EmailAddress(_options.FromEmail, _options.FromName),
                to: new EmailAddress(destinatarioEmail),
                subject: $"Crie sua senha — {marca}",
                plainTextContent:
                    $"Você foi convidado para acessar {marca}.\n\n" +
                    $"Use o link abaixo para criar sua senha. O convite expira em {_options.ConviteValidadeHoras} horas e só pode ser usado uma vez.\n\n" +
                    $"{linkPrimeiroAcesso}\n\nSe você não esperava este convite, ignore este e-mail.",
                htmlContent:
                    $"<p>Você foi convidado para acessar <strong>{WebUtility.HtmlEncode(marca)}</strong>.</p>" +
                    $"<p>Crie sua senha pelo botão abaixo. O convite expira em <strong>{_options.ConviteValidadeHoras} horas</strong> e só pode ser usado uma vez.</p>" +
                    $"<p><a href=\"{linkEscapado}\" style=\"display:inline-block;padding:10px 20px;background:#1a1a1a;color:#fff;text-decoration:none;border-radius:4px;\">Criar minha senha</a></p>" +
                    "<p>Se você não esperava este convite, ignore este e-mail.</p>");

            var client = new SendGridClient(_options.SendGridApiKey);
            Response resposta;
            try
            {
                resposta = await client.SendEmailAsync(mensagem, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(ex, "Falha de transporte ao enviar convite de primeiro acesso via SendGrid.");
                throw new WlPasswordEmailException("Falha ao enviar convite de primeiro acesso.", ex);
            }

            if (!resposta.IsSuccessStatusCode)
            {
                _logger.LogError("SendGrid recusou o convite de primeiro acesso. StatusCode={StatusCode}",
                    (int)resposta.StatusCode);
                throw new WlPasswordEmailException($"SendGrid recusou o envio (status {(int)resposta.StatusCode}).");
            }
        }
    }
}
