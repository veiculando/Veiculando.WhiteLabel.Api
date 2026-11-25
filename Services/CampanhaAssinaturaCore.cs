using System;
using System.Reflection;
using Veiculando.Domain.Entities;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>
/// <c>Campanha.AtualizaStatusAssinatura</c> do Core, chamado na aprovação do pedido pelo App
/// (VEI-SUP-60): todos os pedidos aprovados → campanha Autorizada.
/// </summary>
/// <remarks>
/// Reflexão pelo mesmo motivo do <see cref="CampanhaEdicaoCore"/>: o método é <c>internal</c>
/// no Core fixado em core.ref e não há <c>InternalsVisibleTo</c>. Se a assinatura mudar num
/// bump do core.ref, o tipo falha ao carregar e o teste de aprovação quebra no CI.
/// </remarks>
public static class CampanhaAssinaturaCore
{
    private static readonly MethodInfo Atualiza = typeof(Campanha).GetMethod("AtualizaStatusAssinatura",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null,
            Type.EmptyTypes, modifiers: null)
        ?? throw new MissingMethodException("Campanha.AtualizaStatusAssinatura() não existe no Core fixado em core.ref.");

    public static void AtualizarStatus(Campanha campanha)
    {
        try
        {
            Atualiza.Invoke(campanha, null);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
