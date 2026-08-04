using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Dados de apoio para os testes.
    /// </summary>
    /// <remarks>
    /// Cada teste semeia o que precisa, com ids de afiliada proprios, em vez de
    /// existir um seed global. O banco e compartilhado por toda a suite (subir o
    /// container e caro) e xUnit roda classes em paralelo: seed global viraria
    /// interferencia entre testes, do tipo que so aparece quando a ordem muda.
    /// </remarks>
    public static class Seed
    {
        /// <summary>Senha usada por todos os operadores de teste.</summary>
        public const string SenhaPadrao = "SenhaDeTeste123";

        /// <summary>
        /// Cria um operador da exibidora e devolve o id.
        /// </summary>
        public static async Task<int> OperadorAsync(
            int afiliadaId,
            string email,
            string[]? permissoes = null,
            string nome = "Operador de Teste")
        {
            using var ctx = new VeiculandoDataContext();

            var operador = new WlUsuarioAfiliada(
                nome: nome,
                email: email,
                senhaHash: BC.HashPassword(SenhaPadrao),
                afiliadaId: afiliadaId,
                permissoes: permissoes ?? new string[0]);

            ctx.WlUsuariosAfiliada.Add(operador);
            await ctx.SaveChangesAsync();

            return operador.Id;
        }

        /// <summary>
        /// Cria a afiliada e sua cidade/estado, se ainda nao existirem.
        /// </summary>
        /// <remarks>
        /// Via SQL, e nao pelos construtores do dominio, de proposito. <c>Afiliada</c>
        /// recebe 17 parametros e <c>Peca</c> 18, quase todos irrelevantes para o
        /// que esta sob teste — montar esse grafo em C# encheria os testes de ruido
        /// e os quebraria a cada assinatura que mudasse. O seed nao e o objeto do
        /// teste; os controllers sao.
        ///
        /// <para>O preco e conhecer os nomes das colunas. Em troca, quando o
        /// mapeamento mudar, o seed quebra alto e claro em vez de silenciosamente
        /// gravar em coluna errada.</para>
        /// </remarks>
        public static async Task AfiliadaAsync(int afiliadaId)
        {
            using var ctx = new VeiculandoDataContext();

            await ctx.Database.ExecuteSqlCommandAsync($@"
IF NOT EXISTS (SELECT 1 FROM Estado WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Estado ON;
    INSERT INTO Estado (Id, Sigla, Nome, CodigoIBGE, Latitude, Longitude)
    VALUES (1, 'SP', 'Sao Paulo', 35, -23.55, -46.63);
    SET IDENTITY_INSERT Estado OFF;
END

IF NOT EXISTS (SELECT 1 FROM Cidade WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Cidade ON;
    INSERT INTO Cidade (Id, IdEstado, Nome, Codigo, CodigoIBGE, Latitude, Longitude,
                        DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 1, 'Sao Paulo', 'SP001', 3550308, -23.55, -46.63, GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT Cidade OFF;
END

IF NOT EXISTS (SELECT 1 FROM TipoAfiliada WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT TipoAfiliada ON;
    INSERT INTO TipoAfiliada (Id, Nome) VALUES (1, 'Exibidora');
    SET IDENTITY_INSERT TipoAfiliada OFF;
END

IF NOT EXISTS (SELECT 1 FROM Afiliada WHERE Id = {afiliadaId})
BEGIN
    SET IDENTITY_INSERT Afiliada ON;
    INSERT INTO Afiliada (Id, IdTipoAfiliada, Codigo, Nome, Cnpj, Email,
                          AvaliacaoMedia, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES ({afiliadaId}, 1, 'AF{afiliadaId}', 'Exibidora {afiliadaId}',
            '00000000000191', 'afiliada{afiliadaId}@exemplo.com', 0, GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT Afiliada OFF;
END");
        }

        /// <summary>
        /// Cria um local pertencente a afiliada informada e devolve o id.
        /// </summary>
        public static async Task<int> LocalAsync(
            int afiliadaId,
            string codigo,
            StatusExibicaoLocal status = StatusExibicaoLocal.Ativo,
            FonteOrigemLocal fonte = FonteOrigemLocal.WhiteLabel)
        {
            // Local.Codigo e varchar(12). Passar disso gera
            // "String or binary data would be truncated", que aponta para a coluna
            // mas nao para o teste culpado — a mensagem abaixo aponta.
            if (codigo.Length > 12)
                throw new System.ArgumentException(
                    $"Codigo de local '{codigo}' tem {codigo.Length} caracteres; a coluna aceita 12.",
                    nameof(codigo));

            await AfiliadaAsync(afiliadaId);

            using var ctx = new VeiculandoDataContext();

            await ctx.Database.ExecuteSqlCommandAsync($@"
INSERT INTO Local (IdCidade, IdAfiliada, FonteOrigem, Codigo, Codigointerno, Descricao,
                   Latitude, Longitude, DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (1, {afiliadaId}, {(int)fonte}, '{codigo}', 'INT-{codigo}', 'Local {codigo}',
        -23.55, -46.63, GETDATE(), GETDATE(), {(int)status});");

            return await ctx.Database
                .SqlQuery<int>($"SELECT TOP 1 Id FROM Local WHERE Codigo = '{codigo}' ORDER BY Id DESC")
                .SingleAsync();
        }

        /// <summary>
        /// Cria uma peca no local informado e devolve o id.
        /// </summary>
        public static async Task<int> PecaAsync(
            int localId,
            string codigo,
            StatusExibicaoLocal status = StatusExibicaoLocal.Ativo)
        {
            using var ctx = new VeiculandoDataContext();

            await ctx.Database.ExecuteSqlCommandAsync($@"
IF NOT EXISTS (SELECT 1 FROM TipoSuporte WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT TipoSuporte ON;
    INSERT INTO TipoSuporte (Id, Nome, Codigo, Ordem, StatusExibicao)
    VALUES (1, 'Outdoor', 'OUT', 1, 1);
    SET IDENTITY_INSERT TipoSuporte OFF;
END

INSERT INTO Peca (IdLocal, IdTipoSuporte, FonteOrigem, Codigo, CodigoInterno,
                  Altura, Largura, Juncao, Iluminacao, Semaforo, AnguloDeVisao,
                  Via_Tipo, Via_Faixas, Via_Velociade, Via_Pedestre,
                  RoteiroComercial, Alvara, Periodicidade, ValorPadrao, Promocao,
                  AvaliacaoMedia, AvaliacaoQuantidade,
                  DataCadastro, DataAtualizacao, StatusExibicao)
VALUES ({localId}, 1, 1, '{codigo}', 'INT-{codigo}',
        3, 9, 0, 1, 0, 90,
        0, 2, 60, 0,
        1, 1, 0, 1500, 0,
        0, 0,
        GETDATE(), GETDATE(), {(int)status});");

            return await ctx.Database
                .SqlQuery<int>($"SELECT TOP 1 Id FROM Peca WHERE Codigo = '{codigo}' ORDER BY Id DESC")
                .SingleAsync();
        }

        /// <summary>
        /// Cria um pedido de reserva `Solicitado` com um item, pertencente a
        /// afiliada informada. Devolve (idPedidoReserva, codigo).
        /// </summary>
        /// <remarks>
        /// O grafo e profundo — PedidoReserva -> Pedido -> Campanha -> Agencia,
        /// Cliente e UsuarioAnunciante, mais Periodo e PedidoItem — porque a
        /// resposta de reserva precisa de itens reais: o command enviado ao core
        /// carrega IdPeca e IdPeriodo de cada um.
        ///
        /// <para>As linhas de apoio (perfil, usuario, agencia, cliente, periodo)
        /// sao criadas uma vez com ids fixos e reaproveitadas; so pedido, item e
        /// reserva sao por teste.</para>
        /// </remarks>
        public static async Task<(int Id, string Codigo)> ReservaAsync(
            int afiliadaId,
            string codigo,
            int pecaId,
            StatusPedidoReserva status = StatusPedidoReserva.Solicitado)
        {
            using var ctx = new VeiculandoDataContext();

            await ctx.Database.ExecuteSqlCommandAsync($@"
IF NOT EXISTS (SELECT 1 FROM PerfilUsuario WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT PerfilUsuario ON;
    INSERT INTO PerfilUsuario (Id, Nome, Codigo, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 'Anunciante', 'ANUN', GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT PerfilUsuario OFF;
END

IF NOT EXISTS (SELECT 1 FROM Usuario WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Usuario ON;
    INSERT INTO Usuario (Id, Nome, Email, Senha, StatusAprovacao, Acessos, DataUltimoLogin,
                         EmailConfirmado, DataCadastro, DataAtualizacao, StatusExibicao, IdPerfil)
    VALUES (1, 'Anunciante Teste', 'anunciante@exemplo.com', 'x', 1, 0, GETDATE(),
            1, GETDATE(), GETDATE(), 1, 1);
    SET IDENTITY_INSERT Usuario OFF;
END

IF NOT EXISTS (SELECT 1 FROM UsuarioAnunciante WHERE Id = 1)
    INSERT INTO UsuarioAnunciante (Id) VALUES (1);

IF NOT EXISTS (SELECT 1 FROM Agencia WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Agencia ON;
    INSERT INTO Agencia (Id, Nome, Cnpj, Email, AvaliacaoMedia, BonificacaoVolume,
                         DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 'Agencia Teste', '00000000000191', 'agencia@exemplo.com', 0, 0,
            GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT Agencia OFF;
END

IF NOT EXISTS (SELECT 1 FROM Cliente WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Cliente ON;
    INSERT INTO Cliente (Id, Codigo, Nome, Cnpj, DescontoNegociado, Status,
                         DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 'CLI1', 'Cliente Teste', '00000000000191', 0, 1,
            GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT Cliente OFF;
END

IF NOT EXISTS (SELECT 1 FROM Campanha WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Campanha ON;
    INSERT INTO Campanha (Id, FonteOrigem, IdAgencia, IdUsuarioAnunciante, IdCliente,
                          Nome, Codigo, Status, DataInicioPrevisto, DataFimPrevisto,
                          DescontoNegociado, PermiteConviteAvaliacao,
                          DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 0, 1, 1, 1, 'Campanha Teste', 'CAMP1', 1, GETDATE(), DATEADD(day, 30, GETDATE()),
            0, 0, GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT Campanha OFF;
END

IF NOT EXISTS (SELECT 1 FROM Periodo WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT Periodo ON;
    INSERT INTO Periodo (Id, Codigo, Periodicidade, DataInicio, DataFim, StatusExibicao)
    VALUES (1, 'P1', 0, GETDATE(), DATEADD(day, 14, GETDATE()), 1);
    SET IDENTITY_INSERT Periodo OFF;
END");

            // Pedido
            await ctx.Database.ExecuteSqlCommandAsync($@"
INSERT INTO Pedido (FonteOrigem, IdCampanha, IdCidade, IdPeriodo, IdUsuarioAnunciante,
                    Codigo, Status, StatusPagamento, Revisao,
                    DescontoNegociado, DescontoFinanceiro, ComissaoAgencia, BonificacaoVolume,
                    ComissaoVeiculando, ValorTotalTabela, ValorTotalBruto, ValorDescontoNegociado,
                    ValorDescontoFinanceiro, ValorComissaoAgencia, ValorBonificacaoVolume,
                    ValorComissaoVeiculando, ValorLiquidoVeiculacao, ValorLiquidoAnunciante,
                    DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (0, 1, 1, 1, 1, 'PED-{codigo}', 0, 0, 1,
        0, 0, 0, 0,
        0, 1000, 1000, 0,
        0, 0, 0,
        0, 1000, 1000,
        GETDATE(), GETDATE(), 1);");

            var pedidoId = await ctx.Database
                .SqlQuery<int>($"SELECT TOP 1 Id FROM Pedido WHERE Codigo = 'PED-{codigo}' ORDER BY Id DESC")
                .SingleAsync();

            await ctx.Database.ExecuteSqlCommandAsync($@"
INSERT INTO PedidoItem (IdPedido, IdPeca, IdPeriodo, Status,
                        DescontoNegociado, ValorTabela, ValorBruto, ValorDescontoNegociado,
                        ValorDescontoFinanceiro, ValorComissaoAgencia, ValorBonificacaoVolume,
                        ValorComissaoVeiculando, ValorLiquidoVeiculacao, ValorLiquidoAnunciante)
VALUES ({pedidoId}, {pecaId}, 1, 0,
        0, 1000, 1000, 0,
        0, 0, 0,
        0, 1000, 1000);

INSERT INTO PedidoReserva (IdPedido, IdAfiliada, Codigo, Status,
                           ValorTotalBruto, ValorDesconto, ValorLiquidoVeiculacao,
                           DataCadastro, DataAtualizacao, StatusExibicao)
VALUES ({pedidoId}, {afiliadaId}, '{codigo}', {(int)status},
        1000, 0, 1000, GETDATE(), GETDATE(), 1);");

            var reservaId = await ctx.Database
                .SqlQuery<int>($"SELECT TOP 1 Id FROM PedidoReserva WHERE Codigo = '{codigo}' ORDER BY Id DESC")
                .SingleAsync();

            var itemId = await ctx.Database
                .SqlQuery<int>($"SELECT TOP 1 Id FROM PedidoItem WHERE IdPedido = {pedidoId} ORDER BY Id DESC")
                .SingleAsync();

            await ctx.Database.ExecuteSqlCommandAsync(
                $"INSERT INTO PedidoReservaItem (IdPedidoItem, IdPedidoReserva, Status) VALUES ({itemId}, {reservaId}, 0);");

            return (reservaId, codigo);
        }

        /// <summary>Espelha StatusPedidoReservaEnum.</summary>
        public enum StatusPedidoReserva
        {
            Cancelado = -2,
            Revisado = -1,
            Solicitado = 0,
            Confirmado = 1,
            ItensIndisponiveis = 2,
        }

        /// <summary>Espelha StatusExibicaoEnum para o seed nao depender do enum do dominio.</summary>
        public enum StatusExibicaoLocal
        {
            Deletado = -1,
            Inativo = 0,
            Ativo = 1,
            AprovacaoPendente = 2,
        }

        /// <summary>Espelha FonteOrigemEnum.</summary>
        public enum FonteOrigemLocal
        {
            Core = 0,
            WhiteLabel = 1,
        }
    }
}
