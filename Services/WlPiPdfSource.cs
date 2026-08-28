using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>PDF de uma PI já materializado, pronto para o BFF devolver.</summary>
    public sealed record WlPiPdf(byte[] Conteudo, string ContentType, string NomeArquivo);

    /// <summary>
    /// Origem do PDF de PI. O BFF é o único componente que fala com ela.
    /// </summary>
    /// <remarks>
    /// <para><b>Por que isto não é uma URL entregue ao browser.</b> O
    /// <c>Veiculando.FileServer</c> não tem <c>[Authorize]</c> — nem no
    /// controller nem nas actions — e a busca é
    /// <c>RetornaPIPorCodigo(codigo)</c>, sem <c>AfiliadaId</c> e sem identidade
    /// do chamador. Quem alcançar a rota com um código válido recebe o PDF de
    /// qualquer afiliada. Publicar o host do FileServer no payload da listagem
    /// (que era o que esta classe substitui) transformava isso num IDOR aberto:
    /// bastava enumerar códigos.</para>
    ///
    /// <para>Como o FileServer não tem onde aplicar o recorte de tenant — ele é
    /// compartilhado com o fluxo de agência, que tem outro modelo de identidade,
    /// e não resolve o Host do WhiteLabel (ADR-WL-008) — a barreira precisa ficar
    /// onde o <c>AfiliadaId</c> já existe, que é o BFF.</para>
    /// </remarks>
    public interface IWlPiPdfSource
    {
        /// <returns>O PDF, ou <c>null</c> se a origem não conhece esse código.</returns>
        Task<WlPiPdf?> ObterAsync(string codigo, CancellationToken ct);
    }

    /// <inheritdoc />
    public sealed class FileServerPiPdfSource : IWlPiPdfSource
    {
        private readonly HttpClient _http;

        public FileServerPiPdfSource(HttpClient http) => _http = http;

        public async Task<WlPiPdf?> ObterAsync(string codigo, CancellationToken ct)
        {
            // Rota real do FileServer, em
            // Veiculando.FileServer/Controllers/PedidoInsercaoController:
            // `pedido-insercao/pi-exibidora/{codigo}` — com hífen e chaveada por
            // CÓDIGO. O payload anterior montava
            // `pedidoinsercao/detalhes/{Id}`, que não corresponde a rota alguma;
            // o botão "Abrir PDF" da tela dava 404 desde sempre.
            var rota = $"pedido-insercao/pi-exibidora/{Uri.EscapeDataString(codigo)}";

            using var resposta = await _http.GetAsync(rota, ct);

            if (resposta.StatusCode == HttpStatusCode.NotFound)
                return null;

            resposta.EnsureSuccessStatusCode();

            var bytes = await resposta.Content.ReadAsByteArrayAsync(ct);

            // Nome derivado do código, não do que o FileServer sugerir: o
            // Content-Disposition dele carrega `{Codigo}_{Ticks}.pdf` e repassá-lo
            // significaria confiar num cabeçalho de um serviço que não autentica
            // ninguém.
            return new WlPiPdf(bytes, "application/pdf", $"PI-{codigo}.pdf");
        }
    }
}
