using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Veiculando.Domain.Commands.Inputs;
using Veiculando.Domain.Commands.Inputs.Pedidos;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services
{
    public interface ICoreCadastroService
    {
        Task<CoreRespostaCadastro> SalvarLocalAsync(LocalCadastroCommand command, int? wlUsuarioId);
        Task<CoreRespostaCadastro> SalvarPecaAsync(PecaCadastroCommand command, int? wlUsuarioId);
        Task<CoreRespostaCadastro> ResponderReservaAsync(PedidoReservaRespostaCommand command);
    }

    /// <summary>
    /// Resultado bruto de um cadastro no core, para o controller decidir o
    /// status HTTP sem que este serviço dependa de MVC.
    /// </summary>
    public class CoreRespostaCadastro
    {
        public bool Sucesso { get; set; }
        public int StatusCode { get; set; }
        public string Corpo { get; set; }
    }

    /// <summary>
    /// Encaminha cadastros de local e peça para a API do Veiculando Core,
    /// autenticado como a conta de serviço da instância.
    /// </summary>
    /// <remarks>
    /// Responsabilidade deste serviço: preencher os campos que o cliente
    /// <b>não</b> pode escolher. Em particular <c>IdAfiliada</c> vem sempre do
    /// <see cref="ITenantContext"/> e nunca do payload — caso contrário um
    /// operador poderia cadastrar em outra exibidora só trocando um número no
    /// corpo da requisição.
    ///
    /// <para>A trilha de origem (ADR-WL-003) é aplicada aqui, num único ponto,
    /// para nenhum endpoint futuro esquecer de marcar a procedência: sem
    /// <c>FonteOrigem = WhiteLabel</c> o KPI de aprovação pendente do dashboard
    /// não enxergaria o registro, já que ele filtra exatamente por esse campo.</para>
    /// </remarks>
    public class CoreCadastroService : ICoreCadastroService
    {
        private readonly IVeiculandoApiClient _apiClient;
        private readonly ITenantContext _tenantContext;

        public CoreCadastroService(IVeiculandoApiClient apiClient, ITenantContext tenantContext)
        {
            _apiClient = apiClient;
            _tenantContext = tenantContext;
        }

        public Task<CoreRespostaCadastro> SalvarLocalAsync(LocalCadastroCommand command, int? wlUsuarioId)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            var afiliadaId = _tenantContext.AfiliadaId;

            command.IdAfiliada = afiliadaId;
            command.FonteOrigem = FonteOrigemEnum.WhiteLabel;
            command.FonteAgenciaId = afiliadaId;
            command.FonteUsuarioId = wlUsuarioId;

            // IdUsuario é sobrescrito pelo core com o id do token da conta de
            // serviço (LocalController.Post faz `input.IdUsuario = UserId`), então
            // o que for enviado aqui é irrelevante — mas mandar 0 deixa explícito
            // que o BFF não tenta influenciar essa escolha.
            command.IdUsuario = 0;

            return EnviarAsync("api/local", command);
        }

        public Task<CoreRespostaCadastro> SalvarPecaAsync(PecaCadastroCommand command, int? wlUsuarioId)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            command.FonteOrigem = FonteOrigemEnum.WhiteLabel;
            command.FonteAgenciaId = _tenantContext.AfiliadaId;
            command.FonteUsuarioId = wlUsuarioId;
            command.IdUsuario = 0;

            // A peça não carrega IdAfiliada: ela pertence a um Local, e o
            // controller já validou que esse local é da afiliada da instância.
            return EnviarAsync("api/peca", command);
        }

        /// <summary>
        /// Encaminha a resposta de um pedido de reserva ao core.
        /// </summary>
        /// <remarks>
        /// Responder reserva não é só mudar o status do pedido. O
        /// <c>PedidoReservaRespostaHandler</c> do core, além de confirmar ou marcar
        /// os itens como indisponíveis, atualiza o <c>PecaPeriodoStatus</c> — a
        /// grade de disponibilidade —, grava quem respondeu e propaga o resultado
        /// para o <c>Pedido</c> pai via <c>AtualizaStatusPedidosDeReserva</c>.
        ///
        /// <para>O BFF fazia nada disso: chamava <c>AtualizaStatus()</c> direto na
        /// entidade, com a coleção <c>Itens</c> nunca carregada. Como o ctor
        /// protegido inicializa a lista vazia, <c>Itens.All(...)</c> era verdadeiro
        /// por vacuidade e a reserva virava <c>Confirmado</c> sem que item algum
        /// fosse olhado — e a grade de disponibilidade continuava intocada, deixando
        /// a peça livre para ser reservada de novo.</para>
        ///
        /// <para><c>IdUsuarioAfiliada</c> não é preenchido aqui: o
        /// <c>PedidoReservaController.Resposta</c> do core o sobrescreve com o id do
        /// token da conta de serviço, igual ao que o <c>LocalController</c> faz com
        /// <c>IdUsuario</c>.</para>
        /// </remarks>
        public Task<CoreRespostaCadastro> ResponderReservaAsync(PedidoReservaRespostaCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            return EnviarAsync("api/pedido-reserva/resposta", command);
        }

        private async Task<CoreRespostaCadastro> EnviarAsync(string rota, object command)
        {
            var client = await _apiClient.GetAuthenticatedClientAsync();

            // Newtonsoft, e não System.Text.Json: os value objects do domínio
            // (Endereco, Geolocalizacao, Cep) têm setters privados e construtores
            // sem sobrecarga vazia. É o mesmo serializador que a API core usa
            // para desserializar o command do outro lado.
            var json = JsonConvert.SerializeObject(command);
            var conteudo = new StringContent(json, Encoding.UTF8, "application/json");

            var resposta = await client.PostAsync(rota, conteudo);
            var corpo = await resposta.Content.ReadAsStringAsync();

            return new CoreRespostaCadastro
            {
                Sucesso = resposta.IsSuccessStatusCode,
                StatusCode = (int)resposta.StatusCode,
                Corpo = corpo
            };
        }
    }
}
