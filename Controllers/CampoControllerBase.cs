using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Authorize(Policy = AuthorizationSetup.Campo)]
    public abstract class CampoControllerBase : ControllerBase
    {
        // O UUID do aparelho ja foi usado em outra acao, ordem, peca ou operador.
        public const string IdClienteReutilizado = "campo.id_cliente_reutilizado";
        // Outra acao alterou a ordem em todas as tentativas; o app sincroniza e reenvia.
        public const string OrdemAlterada = "campo.ordem_alterada";

        private readonly ITenantContext _tenant;

        protected CampoControllerBase(ITenantContext tenant) => _tenant = tenant;

        // A policy Campo valida esta identidade antes de executar uma action.
        protected int OperadorId => int.Parse(User.FindFirst(CampoAuthController.ClaimUsuarioOperadorId).Value);
        protected int AfiliadaId => _tenant.AfiliadaId;

        protected ConflictObjectResult RecusarCampo(string codigo) => Conflict(new
        {
            codigo,
            message = codigo switch
            {
                RecusaCampo.SomenteHistorico => "Você tem acesso somente à consulta desta ordem.",
                RecusaCampo.NaoAtribuido => "Você não está atribuído a esta ação.",
                RecusaCampo.OrdemCancelada => "A ordem está cancelada.",
                RecusaCampo.OrdemConcluida => "A ordem já está concluída.",
                RecusaCampo.ForaDePendente => "A ordem não está pendente para iniciar.",
                RecusaCampo.ForaDeColagem => "A ordem não está na etapa de colagem.",
                RecusaCampo.PecaInexistente => "A peça não pertence a esta ordem.",
                RecusaCampo.PecaNaoColada => "A colagem desta peça ainda não foi marcada.",
                RecusaCampo.CheckingIndisponivel => "O checking desta peça não está disponível. Avise a exibidora.",
                IdClienteReutilizado => "Este identificador já foi usado em outra ação.",
                OrdemAlterada => "A ordem foi alterada por outra ação. Sincronize e tente novamente.",
                _ => "A ação de campo foi recusada."
            }
        });
    }
}
