using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Veiculando.WhiteLabel.Api.Controllers.Cms;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Garante que os DTOs de api/wl/cms/* e as fixtures de Contratos/cms-*.json
/// descrevem o mesmo JSON.
/// </summary>
/// <remarks>
/// As fixtures são copiadas para a Exibidora (src/testing/contratos/) e viram a
/// base dos testes das telas. Se um lado mudar sem o outro, este teste quebra
/// aqui, em vez de a tela quebrar no preview.
/// </remarks>
public class CmsContratoTests
{
    // As mesmas opções que o AddControllers() usa: o Startup não customiza o
    // serializador, então o padrão web (camelCase, nulos presentes) é o contrato.
    private static readonly JsonSerializerOptions Json = new JsonOptions().JsonSerializerOptions;

    private static readonly string[] CamposBanner =
        { "id", "title", "imageUrl", "tipoDestino", "destino", "htmlPath", "displayOrder", "ativo", "createdAt", "updatedAt" };

    private static readonly string[] CamposMarca =
        { "id", "name", "imageUrl", "displayOrder", "ativo", "createdAt", "updatedAt" };

    private static readonly string[] CamposDepoimento =
        { "id", "author", "role", "company", "content", "avatarUrl", "displayOrder", "ativo", "createdAt", "updatedAt" };

    private static readonly string[] CamposPagina = { "itens", "page", "pageSize", "total", "totalPaginas" };

    public static IEnumerable<object[]> Fixtures() => new[]
    {
        new object[] { "cms-banners-lista", typeof(WlPagina<CmsBannerDto>) },
        new object[] { "cms-banner-detalhe", typeof(CmsBannerDto) },
        new object[] { "cms-banner-salvo-avisos", typeof(CmsSalvoDto<CmsBannerDto>) },
        new object[] { "cms-marcas-lista", typeof(WlPagina<CmsMarcaDto>) },
        new object[] { "cms-marca-detalhe", typeof(CmsMarcaDto) },
        new object[] { "cms-depoimentos-lista", typeof(WlPagina<CmsDepoimentoDto>) },
        new object[] { "cms-depoimento-detalhe", typeof(CmsDepoimentoDto) },
        new object[] { "cms-depoimentos-resumo", typeof(CmsDepoimentosResumoDto) },
        new object[] { "cms-erro", typeof(CmsErroDto) },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_e_o_json_que_o_dto_produz(string fixture, Type tipo)
    {
        // Ida e volta pelo DTO: um campo a mais na fixture some na volta, um a
        // menos aparece como null/0, e uma chave em PascalCase é lida (a leitura
        // não diferencia maiúsculas) mas volta em camelCase. Os três casos
        // quebram a comparação.
        var original = LerFixture(fixture);

        var dto = JsonSerializer.Deserialize(original.ToJsonString(), tipo, Json);
        var serializado = JsonNode.Parse(JsonSerializer.Serialize(dto, tipo, Json));

        JsonNode.DeepEquals(original, serializado).Should().BeTrue(
            $"{fixture}.json precisa ser idêntico ao que o DTO serializa.\nFixture:    {original.ToJsonString()}\nSerializado: {serializado!.ToJsonString()}");
    }

    [Fact]
    public void Todas_as_fixtures_cms_estao_cobertas()
    {
        var cobertas = Fixtures().Select(f => (string)f[0]).ToHashSet();
        var existentes = Directory.GetFiles(DiretorioContratos(), "cms-*.json")
            .Select(Path.GetFileNameWithoutExtension);

        existentes.Should().BeEquivalentTo(cobertas,
            "uma fixture sem teste de serialização pode divergir do DTO sem ninguém notar");
    }

    [Theory]
    [InlineData("cms-banner-detalhe", "banner")]
    [InlineData("cms-marca-detalhe", "marca")]
    [InlineData("cms-depoimento-detalhe", "depoimento")]
    public void Recurso_tem_exatamente_os_campos_do_contrato(string fixture, string recurso)
    {
        // A ida e volta pega divergência entre fixture e DTO, mas não uma mudança
        // feita nos dois ao mesmo tempo. Os nomes abaixo são o contrato combinado
        // com a Exibidora (plano TP-3, seção 1): renomear um deles é mudança de
        // contrato, e o TP-4 precisa ser avisado.
        var esperado = recurso switch
        {
            "banner" => CamposBanner,
            "marca" => CamposMarca,
            _ => CamposDepoimento,
        };

        Chaves(LerFixture(fixture).AsObject()).Should().BeEquivalentTo(esperado);
    }

    [Theory]
    [InlineData("cms-banners-lista", "banner")]
    [InlineData("cms-marcas-lista", "marca")]
    [InlineData("cms-depoimentos-lista", "depoimento")]
    public void Listas_seguem_o_envelope_WlPagina(string fixture, string recurso)
    {
        var pagina = LerFixture(fixture).AsObject();
        Chaves(pagina).Should().BeEquivalentTo(CamposPagina);

        var esperado = recurso switch
        {
            "banner" => CamposBanner,
            "marca" => CamposMarca,
            _ => CamposDepoimento,
        };

        var itens = pagina["itens"]!.AsArray();
        itens.Should().NotBeEmpty();
        foreach (var item in itens)
            Chaves(item!.AsObject()).Should().BeEquivalentTo(esperado);
    }

    [Fact]
    public void Salvo_traz_item_e_avisos()
    {
        var salvo = LerFixture("cms-banner-salvo-avisos").AsObject();
        Chaves(salvo).Should().BeEquivalentTo("item", "avisos");
        salvo["avisos"]!.AsArray().Should().NotBeEmpty("a fixture existe para o front testar a exibição de avisos");
    }

    [Fact]
    public void Salvo_sem_avisos_serializa_lista_vazia_e_nao_null()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(new CmsSalvoDto<CmsMarcaDto> { Item = new CmsMarcaDto() }, Json))!;

        json["avisos"].Should().NotBeNull();
        json["avisos"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Resumo_tem_os_campos_dos_kpis()
    {
        Chaves(LerFixture("cms-depoimentos-resumo").AsObject())
            .Should().BeEquivalentTo("total", "publicados", "ocultos", "novosNoMes", "empresas");
    }

    [Fact]
    public void Fixtures_so_tem_dado_ficticio_e_inativo()
    {
        // O preview grava no Supabase que a LP de produção lê (ADR-CMS-004). Uma
        // fixture copiada para um seed ou um smoke não pode publicar nada nem
        // apontar para um projeto Supabase real.
        foreach (var arquivo in Directory.GetFiles(DiretorioContratos(), "cms-*.json"))
        {
            var texto = File.ReadAllText(arquivo);
            var no = JsonNode.Parse(texto)!;

            foreach (var ativo in Valores(no, "ativo"))
                ativo!.GetValue<bool>().Should().BeFalse($"{Path.GetFileName(arquivo)} tem registro ativo");

            foreach (var url in Valores(no, "imageUrl").Concat(Valores(no, "avatarUrl")).Where(v => v != null))
                new Uri(url!.GetValue<string>()).Host.Should().Be("cms-ficticio.supabase.co",
                    $"{Path.GetFileName(arquivo)} aponta para um host de Storage que não é o fictício");
        }
    }

    [Fact]
    public void Imagem_e_url_publica_completa_e_html_e_caminho_relativo_ao_bucket()
    {
        var hotsite = LerFixture("cms-banner-salvo-avisos")["item"]!;

        hotsite["imageUrl"]!.GetValue<string>().Should().StartWith("https://")
            .And.Contain("/storage/v1/object/public/cms-assets/banners/");
        hotsite["htmlPath"]!.GetValue<string>().Should().MatchRegex("^banners/[0-9a-f-]{36}\\.html$");
        hotsite["destino"].Should().BeNull("banner tipo html não tem destino");
    }

    private static JsonNode LerFixture(string nome) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(DiretorioContratos(), nome + ".json")))!;

    private static string DiretorioContratos() => Path.Combine(AppContext.BaseDirectory, "Contratos");

    private static IEnumerable<string> Chaves(JsonObject obj) => obj.Select(p => p.Key);

    private static IEnumerable<JsonNode> Valores(JsonNode no, string chave)
    {
        switch (no)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj)
                {
                    if (k == chave) yield return v;
                    if (v != null)
                        foreach (var filho in Valores(v, chave)) yield return filho;
                }
                break;
            case JsonArray arr:
                foreach (var item in arr.Where(i => i != null))
                    foreach (var filho in Valores(item!, chave)) yield return filho;
                break;
        }
    }
}
