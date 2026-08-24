using Microsoft.Extensions.DependencyInjection;

namespace Veiculando.WhiteLabel.Api.Configurations
{
    /// <summary>
    /// Policies de autorização granular do BFF, espelhando as claims
    /// "permission" emitidas por AuthController (colunas de WL_Usuario).
    ///
    /// Antes desta classe não havia NENHUMA policy registrada — todo
    /// [Authorize] no BFF equivalia a "qualquer operador autenticado", sem
    /// checar a permissão granular. O frontend (authGuard do Angular) checava
    /// isso, mas o servidor é a única barreira que não pode ser contornada
    /// trocando o payload do JWT decodificado no cliente.
    /// </summary>
    public static class AuthorizationSetup
    {
        public const string PedidoReservaGerenciar = "PedidoReservaGerenciar";
        public const string PedidoInsercaoVisualizar = "PedidoInsercaoVisualizar";

        public static IServiceCollection AddWhiteLabelAuthorization(this IServiceCollection services)
        {
            services.AddAuthorization(options =>
            {
                options.AddPolicy(PedidoReservaGerenciar, policy => policy.RequireClaim("permission", PedidoReservaGerenciar));

                // PI ainda não tem coluna própria em WL_Usuario; reaproveita
                // PedidoReservaGerenciar até existir uma permissão dedicada —
                // registrado explicitamente para não cair no "qualquer autenticado".
                options.AddPolicy(PedidoInsercaoVisualizar, policy => policy.RequireClaim("permission", PedidoReservaGerenciar));
            });

            return services;
        }
    }
}
