using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>
/// Campos de campanha que o modal do App envia para criar (POST checkout/context/campaigns)
/// e para editar (PUT campaigns/{codigo}). Um formato só, para o modal ser um só (VEI-RD-68).
/// </summary>
/// <remarks>Sem DataAnnotations de propósito: o [ApiController] responderia 400 em
/// ProblemDetails antes da action, e o contrato de erro do modal é <c>{ message, details[] }</c>.</remarks>
public class CampanhaAppRequest
{
    public string Name { get; set; }
    public string Product { get; set; }
    public string Job { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal? Budget { get; set; }
}

/// <summary>
/// Validação comum de criar e editar campanha no App (VEI-RD-68e, peer review I2/I3). Toda
/// recusa vira 400 com <c>{ message, details[] }</c>, que o modal mostra no rodapé.
/// </summary>
public static class CampanhaAppValidacao
{
    /// <summary>As mensagens vão para a tela em português; o container roda em cultura invariante.</summary>
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public const string Mensagem = "Não foi possível salvar a campanha.";
    public const decimal VerbaMinima = 1_000m;
    public const decimal VerbaMaxima = 100_000_000m;
    public const decimal PassoVerba = 1_000m;

    /// <summary>Pedidos já feitos nesta exibidora, que a edição não pode deixar de cobrir.</summary>
    public sealed record Comprometido(decimal Valor, DateTime? PrimeiroDia, DateTime? UltimoDia);

    /// <param name="comprometido">Só no PUT: o que a campanha já tem de pedido nesta exibidora.</param>
    public static List<string> Validar(CampanhaAppRequest request, Comprometido comprometido = null)
    {
        var details = new List<string>();
        if (request == null)
        {
            details.Add("Envie os dados da campanha.");
            return details;
        }

        // Verba ≤ 0 é "sem verba", como no Campanha.Update do Core. O mapa do App manda 0 com o
        // campo vazio; normalizado aqui, criar e editar gravam null (VEI-SUP-46).
        if (request.Budget <= 0) request.Budget = null;

        var nome = request.Name?.Trim();
        if (string.IsNullOrEmpty(nome)) details.Add("Informe o nome da Campanha.");
        else if (nome.Length < 3 || nome.Length > 100) details.Add("O nome da Campanha deve ter de 3 a 100 caracteres.");

        var produto = request.Product?.Trim();
        if (string.IsNullOrEmpty(produto)) details.Add("Informe o Produto.");
        else if (produto.Length < 2 || produto.Length > 100) details.Add("O Produto deve ter de 2 a 100 caracteres.");

        if (request.Job?.Trim().Length > 100) details.Add("O Job deve ter no máximo 100 caracteres.");

        var hoje = DateTime.UtcNow.Date;
        if (request.StartDate == default || request.EndDate == default)
            details.Add("Informe o período da campanha.");
        else
        {
            if (request.EndDate.Date < request.StartDate.Date)
                details.Add("A data final deve ser igual ou posterior à data inicial.");
            if (request.EndDate.Date < hoje) details.Add("A data final não pode estar no passado.");
            if (request.EndDate.Date > hoje.AddYears(2)) details.Add("A data final pode ser de no máximo 2 anos a partir de hoje.");
        }

        // Verba é opcional (campanha sem verba devolve verbaDisponivel nulo); informada, segue o modal.
        if (request.Budget.HasValue)
        {
            var verba = request.Budget.Value;
            if (verba < VerbaMinima || verba > VerbaMaxima)
                details.Add("A verba deve ficar entre R$ 1.000 e R$ 100.000.000.");
            else if (verba % PassoVerba != 0)
                details.Add("A verba deve ser múltipla de R$ 1.000.");
        }

        if (comprometido != null)
        {
            if (request.Budget.HasValue && request.Budget.Value < comprometido.Valor)
                details.Add($"A verba não pode ficar abaixo do valor já comprometido em pedidos nesta exibidora " +
                            $"(mínimo R$ {comprometido.Valor.ToString("N2", PtBr)}).");
            if (comprometido.PrimeiroDia.HasValue && request.StartDate != default &&
                request.StartDate.Date > comprometido.PrimeiroDia.Value.Date)
                details.Add($"O período precisa começar até {comprometido.PrimeiroDia.Value.ToString("dd/MM/yyyy", PtBr)}, " +
                            "início do primeiro pedido da campanha.");
            if (comprometido.UltimoDia.HasValue && request.EndDate != default &&
                request.EndDate.Date < comprometido.UltimoDia.Value.Date)
                details.Add($"O período precisa ir até {comprometido.UltimoDia.Value.ToString("dd/MM/yyyy", PtBr)}, " +
                            "fim do último pedido da campanha.");
        }

        return details;
    }

    /// <summary>400 no formato do modal: <c>{ message, details[] }</c>.</summary>
    public static BadRequestObjectResult Erro(string message, IEnumerable<string> details) =>
        new(new { message, details });

    public static BadRequestObjectResult Erro(string message) => Erro(message, new[] { message });
}
