using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;

namespace Veiculando.WhiteLabel.Api.Services;

public sealed record CampoProgressoDto(string Tipo, int Feitos, int Total);
public sealed record CampoPeriodoDto(int Id, string Nome, DateTime De, DateTime Ate);
public sealed record CampoPecaDto(int Id, string Codigo, string Anterior, string Atual, string Motivo,
    DateTimeOffset? DataColagem, DateTimeOffset? DataFotoCampo, double? Latitude, double? Longitude,
    string FotoAnteriorUrl, string ArteAtualUrl, string Tipo);
public sealed record CampoLocalDto(int Id, string Numero, string Codigo, string Logradouro, string Bairro,
    string Cidade, IReadOnlyList<CampoPecaDto> Pecas);

public sealed class CampoOrdemDto
{
    public int Id { get; init; }
    public string NumeroFormatado { get; init; }
    public IReadOnlyList<string> Papeis { get; init; }
    public string Cidade { get; init; }
    public CampoPeriodoDto Periodo { get; init; }
    public string Endereco { get; init; }
    public string Estado { get; init; }
    public CampoProgressoDto Progresso { get; init; }
    public string Acao { get; init; }
    public DateTimeOffset? DataInicio { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CampoLocalDto> Locais { get; set; }
}

public static class CampoOrdensProjecao
{
    public static CampoOrdemDto Linha(OrdemServico ordem, int operadorId)
    {
        var papeis = new List<string>();
        if (ordem.EhColadorAtual(operadorId)) papeis.Add("colador");
        if (ordem.EhFiscalAtual(operadorId)) papeis.Add("fiscal");
        if (ordem.SomenteNoHistorico(operadorId)) papeis.Add("somente_historico");
        var fotos = ordem.Status is StatusOrdemServicoEnum.AguardandoFoto
            or StatusOrdemServicoEnum.FotoEmAndamento or StatusOrdemServicoEnum.Concluida;
        var consulta = ordem.SomenteNoHistorico(operadorId)
            || ordem.Status is StatusOrdemServicoEnum.Cancelada or StatusOrdemServicoEnum.Concluida;
        var primeiroLocal = ordem.Pecas.Where(p => p.Peca?.Local != null)
            .OrderBy(p => p.Peca.Local.Id).Select(p => p.Peca.Local).FirstOrDefault();
        return new CampoOrdemDto
        {
            Id = ordem.Id,
            NumeroFormatado = ordem.NumeroFormatado,
            Papeis = papeis,
            Cidade = string.Join(", ", ordem.Cidades.Select(c => c.Cidade?.Nome).Where(n => n != null).Distinct().OrderBy(n => n)),
            Periodo = ordem.Periodo == null ? null : new CampoPeriodoDto(ordem.IdPeriodo, ordem.Periodo.Nome,
                ordem.Periodo.DataInicio, ordem.Periodo.DataFim),
            Endereco = primeiroLocal?.Endereco == null ? null : string.Join(", ", new[] {
                primeiroLocal.Endereco.Logradouro, primeiroLocal.Endereco.Numero, primeiroLocal.Endereco.Bairro
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
            // Os aliases legados do enum nao sao rotulos do aplicativo.
            Estado = ordem.Status == StatusOrdemServicoEnum.Pendente ? "Pendente" : ordem.Status.NomeNoCampo(),
            Progresso = new CampoProgressoDto(fotos ? "fotos" : "colagens",
                ordem.Pecas.Count(p => fotos ? p.Fotografada : p.Colada), ordem.Pecas.Count),
            Acao = consulta ? "Consulta" : ordem.Status == StatusOrdemServicoEnum.Pendente
                && ordem.DataInicio == null && ordem.EhColadorAtual(operadorId) ? "Iniciar" : "Abrir",
            DataInicio = ordem.DataInicio
        };
    }
}
