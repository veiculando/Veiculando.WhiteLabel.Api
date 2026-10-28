using System;
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

public class CampoOrdensProjecaoTests
{
    [Fact]
    public void Responsavel_legado_recebe_os_dois_papeis_e_pode_iniciar()
    {
        var ordem = Ordem(StatusOrdemServicoEnum.Pendente);
        Set(ordem, nameof(OrdemServico.IdResponsavel), 10);
        var linha = CampoOrdensProjecao.Linha(ordem, 10);
        linha.Papeis.Should().Equal("colador", "fiscal");
        linha.Acao.Should().Be("Iniciar");
        linha.Estado.Should().Be("Pendente");
        linha.NumeroFormatado.Should().Be("OS #0042");
    }

    [Fact]
    public void Historico_nao_permite_acao_e_retorna_somente_historico()
    {
        var ordem = Ordem(StatusOrdemServicoEnum.Colagem);
        Set(ordem, nameof(OrdemServico.HistoricoAtribuicaoIds), "10,11");
        var linha = CampoOrdensProjecao.Linha(ordem, 10);
        linha.Papeis.Should().Equal("somente_historico");
        linha.Acao.Should().Be("Consulta");
    }

    [Fact]
    public void Atribuicao_atual_prevalece_sobre_o_historico()
    {
        var ordem = Ordem(StatusOrdemServicoEnum.Colagem);
        Set(ordem, nameof(OrdemServico.HistoricoAtribuicaoIds), "10");
        Set(ordem, nameof(OrdemServico.IdFiscal), 10);
        var linha = CampoOrdensProjecao.Linha(ordem, 10);
        linha.Papeis.Should().Equal("fiscal");
        linha.Acao.Should().Be("Abrir");
    }

    [Theory]
    [InlineData(StatusOrdemServicoEnum.Cancelada)]
    [InlineData(StatusOrdemServicoEnum.Concluida)]
    public void Estado_final_e_somente_consulta(StatusOrdemServicoEnum estado)
    {
        var ordem = Ordem(estado);
        Set(ordem, nameof(OrdemServico.IdColador), 10);
        CampoOrdensProjecao.Linha(ordem, 10).Acao.Should().Be("Consulta");
    }

    [Theory]
    [InlineData(StatusOrdemServicoEnum.Colagem, "colagens", 1)]
    [InlineData(StatusOrdemServicoEnum.AguardandoFoto, "fotos", 1)]
    [InlineData(StatusOrdemServicoEnum.FotoEmAndamento, "fotos", 1)]
    [InlineData(StatusOrdemServicoEnum.Concluida, "fotos", 1)]
    public void Progresso_troca_de_colagens_para_fotos(StatusOrdemServicoEnum estado, string tipo, int feitos)
    {
        var ordem = Ordem(estado);
        var peca = (OrdemServicoPeca)Activator.CreateInstance(typeof(OrdemServicoPeca), true);
        Set(peca, nameof(OrdemServicoPeca.DataColagem), DateTimeOffset.UtcNow);
        Set(peca, nameof(OrdemServicoPeca.DataFotoCampo), DateTimeOffset.UtcNow);
        ordem.Pecas.Add(peca);
        ordem.Pecas.Add((OrdemServicoPeca)Activator.CreateInstance(typeof(OrdemServicoPeca), true));
        var progresso = CampoOrdensProjecao.Linha(ordem, 10).Progresso;
        progresso.Tipo.Should().Be(tipo);
        progresso.Feitos.Should().Be(feitos);
        progresso.Total.Should().Be(2);
    }

    [Fact]
    public void Fiscal_nao_recebe_a_acao_iniciar()
    {
        var ordem = Ordem(StatusOrdemServicoEnum.Pendente);
        Set(ordem, nameof(OrdemServico.IdFiscal), 10);
        CampoOrdensProjecao.Linha(ordem, 10).Acao.Should().Be("Abrir");
    }

    private static OrdemServico Ordem(StatusOrdemServicoEnum estado)
    {
        var ordem = (OrdemServico)Activator.CreateInstance(typeof(OrdemServico), true);
        Set(ordem, nameof(OrdemServico.Numero), 42);
        Set(ordem, nameof(OrdemServico.Status), estado);
        return ordem;
    }

    private static void Set(object obj, string property, object value) =>
        obj.GetType().GetProperty(property).SetValue(obj, value);
}
