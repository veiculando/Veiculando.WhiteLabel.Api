using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Substitui a API do core nas chamadas que o BFF delega.
    /// </summary>
    /// <remarks>
    /// Cadastro de local/peca e resposta de reserva nao sao executados pelo BFF:
    /// ele encaminha para o core autenticado como conta de servico
    /// (<c>CoreCadastroService</c>). Num teste de integracao do BFF nao ha core
    /// para atender, e deixar a chamada sair de verdade tornaria a suite
    /// dependente de rede e de um ambiente externo.
    ///
    /// <para>O stub registra o que foi enviado, para o teste poder afirmar sobre o
    /// <b>contrato</b> — que e o que cabe verificar aqui. Exemplo: o
    /// <c>ResponderReserva</c> precisa mandar <c>IdPeca</c> e <c>IdPeriodo</c> de
    /// cada item, e <c>IdsPecaSugerida</c> como array vazio (o handler do core
    /// acessa <c>.Length</c> sem checar nulidade). Se isso regredir, o teste pega
    /// aqui em vez de virar 500 em producao.</para>
    /// </remarks>
    public sealed class CoreApiStub : HttpMessageHandler
    {
        private readonly List<RequisicaoCapturada> _requisicoes = new();

        /// <summary>Requisicoes que o BFF enviou ao core, na ordem.</summary>
        public IReadOnlyList<RequisicaoCapturada> Requisicoes => _requisicoes;

        /// <summary>
        /// Resposta a devolver. Trocavel pelo teste para exercitar o caminho de
        /// erro — o BFF repassa corpo e status do core nesse caso.
        /// </summary>
        public Func<RequisicaoCapturada, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"sucesso\":true}", System.Text.Encoding.UTF8, "application/json")
            };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var corpo = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var capturada = new RequisicaoCapturada(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                corpo);

            _requisicoes.Add(capturada);

            return Responder(capturada);
        }

        public void Limpar() => _requisicoes.Clear();

        public sealed record RequisicaoCapturada(string Metodo, string Url, string Corpo);
    }
}
