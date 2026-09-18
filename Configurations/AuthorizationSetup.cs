using System;
using Microsoft.Extensions.DependencyInjection;

namespace Veiculando.WhiteLabel.Api.Configurations
{
    /// <summary>
    /// Policies de autorização granular do painel WhiteLabel (TP-R2, blocker B3).
    /// </summary>
    /// <remarks>
    /// Antes disso os controllers usavam apenas <c>[Authorize]</c> genérico: qualquer
    /// operador autenticado — inclusive um com <c>PermissoesRaw</c> vazio — chamava
    /// qualquer endpoint de escrita direto por <c>curl</c>. O menu escondido e o
    /// <c>authGuard</c> do frontend são UI; não protegem a API.
    ///
    /// <para>Os nomes das policies são os mesmos identificadores de
    /// <c>WlPermissoesValidas</c>, e a claim exigida é <c>permission</c> — que é o
    /// que o <c>AuthController</c> emite no login e no refresh
    /// (<c>new Claim("permission", perm)</c>). Um descasamento aqui bloquearia
    /// silenciosamente todos os operadores legítimos, então os dois lados usam a
    /// mesma constante.</para>
    /// </remarks>
    public static class AuthorizationSetup
    {
        /// <summary>
        /// Tipo da claim de permissão emitida pelo <c>AuthController</c>.
        /// </summary>
        public const string ClaimPermissao = "permission";

        public const string PecaGerenciar = "PecaGerenciar";
        // "Checking" foi reconciliado para "CheckingGerenciar" (VEI-RD-93, único nome
        // sem verbo no repo). Usuários com a claim antiga são migrados via
        // RenomearCheckingEGrantProgramacaoVisualizar no domínio.
        public const string CheckingGerenciar = "CheckingGerenciar";
        public const string PedidoReservaGerenciar = "PedidoReservaGerenciar";
        public const string PedidoInsercaoGerenciar = "PedidoInsercaoGerenciar";
        public const string UsuarioAfiliadaGerenciar = "UsuarioAfiliadaGerenciar";
        public const string ClienteGerenciar = "ClienteGerenciar";
        public const string PedidoCriar = "PedidoCriar";
        public const string ProgramacaoVisualizar = "ProgramacaoVisualizar";
        public const string FinanceiroVisualizar = "FinanceiroVisualizar";
        public const string RelatorioExportar = "RelatorioExportar";

        /// <summary>Lista canônica de permissões (VEI-RD-93) — espelha WlPermissoesValidas do domínio.</summary>
        public static readonly string[] Todas =
        {
            PecaGerenciar,
            CheckingGerenciar,
            PedidoReservaGerenciar,
            PedidoInsercaoGerenciar,
            UsuarioAfiliadaGerenciar,
            ClienteGerenciar,
            PedidoCriar,
            ProgramacaoVisualizar,
            FinanceiroVisualizar,
            RelatorioExportar
        };

        public static IServiceCollection AddWlAuthorization(this IServiceCollection services)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            services.AddAuthorization(options =>
            {
                foreach (var permissao in Todas)
                {
                    // Nome da policy == nome da permissão: evita uma tabela de
                    // tradução entre os dois que precisaria ser mantida em sincronia.
                    options.AddPolicy(permissao, policy =>
                        policy.RequireAuthenticatedUser()
                              .RequireClaim(ClaimPermissao, permissao));
                }
            });

            return services;
        }
    }
}
