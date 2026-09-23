using System.Data.Entity;
using System.Globalization;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Seed de Anunciantes e Tipos de Suporte/Formatos (Sprint 10.5 BE-1/BE-2).
    /// </summary>
    /// <remarks>
    /// Via SQL, pelo mesmo motivo de <see cref="Seed"/>: os construtores do
    /// domínio pedem grafos inteiros irrelevantes para o que está sob teste.
    /// Chaves naturais (CNPJ, código) são únicas por teste — o banco é
    /// compartilhado pela suíte inteira.
    /// </remarks>
    public static class SeedCadastros
    {
        /// <summary>
        /// <see cref="Seed"/> cria "Cliente Teste" e o TipoSuporte "Outdoor" com
        /// Id 1 fixo — mas só SE o Id 1 estiver livre. Sem isto, quando uma classe
        /// daqui roda primeiro, a identity entrega o Id 1 a um registro deste
        /// arquivo e os testes de PI passam a ver outro anunciante.
        /// </summary>
        private static async Task GarantirLinhasCanonicasAsync(VeiculandoDataContext ctx)
        {
            await ctx.Database.ExecuteSqlCommandAsync(@"
IF NOT EXISTS (SELECT 1 FROM Cliente WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Cliente ON;
    INSERT INTO Cliente (Id, Codigo, Nome, Cnpj, DescontoNegociado, Status,
                         DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 'CLI1', 'Cliente Teste', '00000000000191', 0, 1,
            GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT Cliente OFF;
END

IF NOT EXISTS (SELECT 1 FROM TipoSuporte WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT TipoSuporte ON;
    INSERT INTO TipoSuporte (Id, Nome, Codigo, Ordem, StatusExibicao)
    VALUES (1, 'Outdoor', 'OUT', 1, 1);
    SET IDENTITY_INSERT TipoSuporte OFF;
END");
        }

        public static async Task<int> ClienteAsync(string cnpj, string codigo, string nome, string razaoSocial = null)
        {
            using var ctx = new VeiculandoDataContext();
            await GarantirLinhasCanonicasAsync(ctx);
            await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO Cliente (Codigo, Nome, RazaoSocial, Cnpj, Cidade, Uf, DescontoNegociado, Status,
                     DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (@p0, @p1, @p2, @p3, 'Sao Paulo', 'SP', 0, 1, GETDATE(), GETDATE(), 1);",
                codigo, nome, razaoSocial ?? nome + " Comercio Ltda", cnpj);

            return await ctx.Database
                .SqlQuery<int>("SELECT TOP 1 Id FROM Cliente WHERE Cnpj = @p0 ORDER BY Id DESC", cnpj)
                .SingleAsync();
        }

        public static async Task VinculoClienteAsync(int afiliadaId, int clienteId, bool ativo = true, int? segmentoId = null, string email = null)
        {
            await Seed.AfiliadaAsync(afiliadaId);
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO AfiliadaCliente (FonteOrigem, IdAfiliada, IdCliente, Status, DataVinculo, IdSegmento, Email,
                             DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (1, @p0, @p1, @p2, GETDATE(), @p3, @p4, GETDATE(), GETDATE(), 1);",
                afiliadaId, clienteId, ativo ? 1 : 0, (object)segmentoId ?? System.DBNull.Value, (object)email ?? System.DBNull.Value);
        }

        public static async Task<int> SegmentoAsync(string nome)
        {
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync("INSERT INTO Segmento (IdSegmentoPai, Nome) VALUES (NULL, @p0);", nome);
            return await ctx.Database
                .SqlQuery<int>("SELECT TOP 1 Id FROM Segmento WHERE Nome = @p0 ORDER BY Id DESC", nome)
                .SingleAsync();
        }

        /// <summary>
        /// PI de <paramref name="valor"/> para o cliente na afiliada. Exige o grafo
        /// de apoio de <see cref="Seed.ReservaAsync"/> (agência 1, usuário 1,
        /// cidade 1, período 1) — chame-o antes.
        /// </summary>
        public static async Task<int> PiDoClienteAsync(int afiliadaId, int clienteId, string codigo, decimal valor, int status = 0)
        {
            using var ctx = new VeiculandoDataContext();
            var v = valor.ToString(CultureInfo.InvariantCulture);

            await ctx.Database.ExecuteSqlCommandAsync($@"
INSERT INTO Campanha (FonteOrigem, IdAgencia, IdUsuarioAnunciante, IdCliente,
                      Nome, Codigo, Status, DataInicioPrevisto, DataFimPrevisto,
                      DescontoNegociado, PermiteConviteAvaliacao,
                      DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (0, 1, 1, {clienteId}, 'Campanha {codigo}', 'C{codigo}', 1, GETDATE(), DATEADD(day, 30, GETDATE()),
        0, 0, GETDATE(), GETDATE(), 1);

DECLARE @campanha int = SCOPE_IDENTITY();

INSERT INTO Pedido (FonteOrigem, IdCampanha, IdCidade, IdPeriodo, IdUsuarioAnunciante,
                    Codigo, Status, StatusPagamento, Revisao,
                    DescontoNegociado, DescontoFinanceiro, ComissaoAgencia, BonificacaoVolume,
                    ComissaoVeiculando, ValorTotalTabela, ValorTotalBruto, ValorDescontoNegociado,
                    ValorDescontoFinanceiro, ValorComissaoAgencia, ValorBonificacaoVolume,
                    ValorComissaoVeiculando, ValorLiquidoVeiculacao, ValorLiquidoAnunciante,
                    DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (0, @campanha, 1, 1, 1, 'P{codigo}', 0, 0, 1,
        0, 0, 0, 0, 0, {v}, {v}, 0, 0, 0, 0, 0, {v}, {v},
        GETDATE(), GETDATE(), 1);

DECLARE @pedido int = SCOPE_IDENTITY();

INSERT INTO PedidoInsercao (IdPedido, IdAfiliada, Codigo, Status, StatusPagamento,
                            ValorTotalTabela, ValorTotalBruto, ValorDescontoNegociado,
                            ValorDescontoFinanceiro, ValorComissaoAgencia, ValorBonificacaoVolume,
                            ValorComissaoVeiculando, ValorLiquidoVeiculacao, ValorLiquidoAnunciante,
                            DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (@pedido, {afiliadaId}, 'PI{codigo}', {status}, 0,
        {v}, {v}, 0, 0, 0, 0, 0, {v}, {v},
        GETDATE(), GETDATE(), 1);");

            return await ctx.Database
                .SqlQuery<int>($"SELECT TOP 1 Id FROM Campanha WHERE Codigo = 'C{codigo}' ORDER BY Id DESC")
                .SingleAsync();
        }

        public static async Task<int> TipoSuporteAsync(string codigo, string nome, bool ativoNoCatalogo = true)
        {
            using var ctx = new VeiculandoDataContext();
            await GarantirLinhasCanonicasAsync(ctx);
            await ctx.Database.ExecuteSqlCommandAsync(
                "INSERT INTO TipoSuporte (Nome, Codigo, Ordem, StatusExibicao) VALUES (@p0, @p1, 1, @p2);",
                nome, codigo, ativoNoCatalogo ? 1 : 0);
            return await ctx.Database
                .SqlQuery<int>("SELECT TOP 1 Id FROM TipoSuporte WHERE Codigo = @p0 ORDER BY Id DESC", codigo)
                .SingleAsync();
        }

        /// <summary>Habilitação direta, para montar cenários (o endpoint é o objeto de outros testes).</summary>
        public static async Task<int> HabilitacaoAsync(int afiliadaId, int tipoId, bool ativa = true)
        {
            await Seed.AfiliadaAsync(afiliadaId);
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO AfiliadaTipoSuporte (FonteOrigem, IdAfiliada, IdTipoSuporte, Status, DataVinculo,
                                 DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (1, @p0, @p1, @p2, GETDATE(), GETDATE(), GETDATE(), 1);", afiliadaId, tipoId, ativa ? 1 : 0);
            return await ctx.Database
                .SqlQuery<int>("SELECT Id FROM AfiliadaTipoSuporte WHERE IdAfiliada = @p0 AND IdTipoSuporte = @p1", afiliadaId, tipoId)
                .SingleAsync();
        }

        /// <summary>Formato do catálogo global. Dimensões únicas por teste evitam a deduplicação.</summary>
        public static async Task<int> FormatoAsync(int midiaTipo, decimal largura, decimal altura,
            short resolucaoLargura = 0, short resolucaoAltura = 0, short duracao = 0)
        {
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO Formato (MidiaTipo, Largura, Altura, ResolucaoLargura, ResolucaoAltura, Duracao,
                     ExtensoesAceitas, DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (@p0, @p1, @p2, @p3, @p4, @p5, '.pdf', GETDATE(), GETDATE(), 1);",
                midiaTipo, largura, altura, resolucaoLargura, resolucaoAltura, duracao);
            return await ctx.Database
                .SqlQuery<int>(@"SELECT TOP 1 Id FROM Formato WHERE MidiaTipo = @p0 AND Largura = @p1 AND Altura = @p2
                                  AND ResolucaoLargura = @p3 AND ResolucaoAltura = @p4 AND Duracao = @p5 ORDER BY Id",
                    midiaTipo, largura, altura, resolucaoLargura, resolucaoAltura, duracao)
                .SingleAsync();
        }

        public static async Task AssociarFormatoAsync(int habilitacaoId, int formatoId, bool ativo = true)
        {
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO AfiliadaTipoSuporteFormato (FonteOrigem, IdAfiliadaTipoSuporte, IdFormato, Status,
                                        DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (1, @p0, @p1, @p2, GETDATE(), GETDATE(), 1);", habilitacaoId, formatoId, ativo ? 1 : 0);
        }

        /// <summary>Aponta uma peça do seed padrão para tipo/formato específicos.</summary>
        public static async Task PecaComTipoEFormatoAsync(int pecaId, int tipoId, int? formatoId)
        {
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(
                "UPDATE Peca SET IdTipoSuporte = @p0, IdFormatoArteFinal = @p1 WHERE Id = @p2;",
                tipoId, (object)formatoId ?? System.DBNull.Value, pecaId);
        }

        public static async Task<int> ContarAsync(string sql, params object[] parametros)
        {
            using var ctx = new VeiculandoDataContext();
            return await ctx.Database.SqlQuery<int>(sql, parametros).SingleAsync();
        }

        public static async Task ExecutarAsync(string sql, params object[] parametros)
        {
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(sql, parametros);
        }
    }
}
