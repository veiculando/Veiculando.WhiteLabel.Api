using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Dublê de <see cref="IWlPasswordEmailSender"/> para os testes de
    /// recuperação de senha: captura o que seria enviado, sem tocar rede nem
    /// exigir uma API key de verdade.
    /// </summary>
    public sealed class FakeWlPasswordEmailSender : IWlPasswordEmailSender
    {
        private readonly ConcurrentQueue<EnvioCapturado> _envios = new();
        private readonly ConcurrentQueue<ConviteCapturado> _convites = new();

        /// <summary>
        /// Quando <c>true</c>, o próximo envio lança <see cref="WlPasswordEmailException"/> —
        /// simula o SendGrid recusando ou falhando o envio.
        /// </summary>
        public bool FalharProximoEnvio { get; set; }

        public IReadOnlyCollection<EnvioCapturado> Envios => _envios.ToArray();
        public IReadOnlyCollection<ConviteCapturado> Convites => _convites.ToArray();

        public Task EnviarConviteAsync(
            string destinatarioEmail,
            string nomeExibicaoMarca,
            string linkPrimeiroAcesso,
            CancellationToken cancellationToken = default)
        {
            if (FalharProximoEnvio)
            {
                FalharProximoEnvio = false;
                throw new WlPasswordEmailException("Falha simulada de envio (teste).");
            }

            _convites.Enqueue(new ConviteCapturado(destinatarioEmail, nomeExibicaoMarca, linkPrimeiroAcesso));
            return Task.CompletedTask;
        }

        public Task EnviarRecuperacaoAsync(
            string destinatarioEmail,
            string nomeExibicaoMarca,
            string linkReset,
            CancellationToken cancellationToken = default)
        {
            if (FalharProximoEnvio)
            {
                FalharProximoEnvio = false;
                throw new WlPasswordEmailException("Falha simulada de envio (teste).");
            }

            _envios.Enqueue(new EnvioCapturado(destinatarioEmail, nomeExibicaoMarca, linkReset));
            return Task.CompletedTask;
        }

        public sealed record EnvioCapturado(string DestinatarioEmail, string NomeExibicaoMarca, string LinkReset);
        public sealed record ConviteCapturado(string DestinatarioEmail, string NomeExibicaoMarca, string LinkPrimeiroAcesso);
    }
}
