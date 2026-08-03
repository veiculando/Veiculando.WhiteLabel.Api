using Microsoft.AspNetCore.Mvc;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Base dos controllers que delegam cadastro ao core (Locais e Peças).
    /// </summary>
    /// <remarks>
    /// <c>WlUsuarioId</c> e <c>RepassarResposta</c> existiam duplicados,
    /// caractere por caractere, nos dois controllers. Duas cópias do mesmo
    /// contrato com o core é uma a mais do que o necessário: se o formato da
    /// resposta de erro mudar, é fácil corrigir uma e esquecer a outra, e o
    /// sintoma seria uma tela mostrando 500 genérico enquanto a irmã mostra a
    /// notificação de validação correta.
    /// </remarks>
    public abstract class WlCoreProxyControllerBase : ControllerBase
    {
        /// <summary>
        /// Id do operador WhiteLabel autenticado, para a trilha de origem.
        /// </summary>
        protected int? WlUsuarioId =>
            int.TryParse(User.FindFirst("WlUsuarioId")?.Value, out var id) ? id : (int?)null;

        /// <summary>
        /// Repassa a resposta do core preservando o status e o corpo, para que as
        /// notificações do domínio (mensagens de validação) cheguem à tela em vez
        /// de virarem um 500 genérico.
        /// </summary>
        protected IActionResult RepassarResposta(CoreRespostaCadastro resposta)
        {
            if (resposta.Sucesso)
                return Content(resposta.Corpo ?? "{}", "application/json");

            return StatusCode(resposta.StatusCode, new
            {
                message = "A operação foi recusada pelo Veiculando Core.",
                detalhe = resposta.Corpo
            });
        }
    }
}
