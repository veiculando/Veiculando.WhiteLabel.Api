using System;
using System.Reflection;
using Veiculando.Domain.Entities;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>
/// Edição de campanha pelo método do próprio Core, <c>Campanha.Update</c> (decisão do PO, 09/11).
/// </summary>
/// <remarks>
/// <para>Por que não o <c>CampanhaCadastroHandler</c>: ele exige a permissão legada
/// <c>CriarCampanha</c>, que nenhum responsável comercial do WhiteLabel recebe, e
/// <c>Cliente.Status == Aprovado</c>, que a carteira da Prospecção não garante. No App, quem
/// pode editar já foi decidido pelo escopo da sessão.</para>
/// <para>Por que reflexão: <c>Campanha.Update</c> é <c>internal</c> no Core 2c014ea5 e não há
/// <c>InternalsVisibleTo</c>. Sem PR no Core nem bump do core.ref, este é o único caminho para
/// usar a regra dele (verba ≤ 0 vira nula, <c>RegistrarAlteracao</c>, <c>Validate</c>). O
/// método é resolvido uma vez; se a assinatura mudar num bump do core.ref, o tipo falha ao
/// carregar e o teste <c>Edicao_usa_o_Update_do_Core</c> quebra no CI.</para>
/// <para>Quando o Core tornar o método público, trocar esta classe pela chamada direta.</para>
/// </remarks>
public static class CampanhaEdicaoCore
{
    private static readonly MethodInfo Update = typeof(Campanha).GetMethod("Update",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null,
            new[]
            {
                typeof(UsuarioAnunciante), typeof(string), typeof(string), typeof(string),
                typeof(DateTime), typeof(DateTime), typeof(decimal?)
            }, modifiers: null)
        ?? throw new MissingMethodException(
            "Campanha.Update(UsuarioAnunciante, string, string, string, DateTime, DateTime, decimal?) " +
            "não existe no Core fixado em core.ref.");

    public static void Aplicar(Campanha campanha, UsuarioAnunciante responsavel, string nome, string produto,
        string job, DateTime dataInicio, DateTime dataFim, decimal? verba)
    {
        try
        {
            Update.Invoke(campanha, new object[] { responsavel, nome, produto, job, dataInicio, dataFim, verba });
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
