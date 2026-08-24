using System.Collections.Generic;
using System.Linq;
using Moq;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Contracts.PedidosReserva;
using Veiculando.WhiteLabel.Api.Tests.TestHelpers;
using Veiculando.WhiteLabel.Api.Validation;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests.Validation
{
    public class PedidoReservaRespostaValidatorTests
    {
        // --- Helpers para construir grafo de domínio sem depender de EF/DB ---

        private static PedidoItem BuildPedidoItem(int idPeca, int idPeriodo)
        {
            var item = ReflectionTestHelpers.CreateUninitialized<PedidoItem>();
            ReflectionTestHelpers.SetPrivate(item, "IdPeca", idPeca);
            ReflectionTestHelpers.SetPrivate(item, "IdPeriodo", idPeriodo);
            return item;
        }

        private static PedidoReservaItem BuildItem(int idPedidoItem, int idPeca, int idPeriodo, StatusPedidoReservaItemEnum status = StatusPedidoReservaItemEnum.Solicitado)
        {
            var pedidoItem = BuildPedidoItem(idPeca, idPeriodo);
            ReflectionTestHelpers.SetPrivate(pedidoItem, "Id", idPedidoItem);

            var item = ReflectionTestHelpers.CreateUninitialized<PedidoReservaItem>();
            ReflectionTestHelpers.SetPrivate(item, "IdPedidoItem", idPedidoItem);
            ReflectionTestHelpers.SetPrivate(item, "PedidoItem", pedidoItem);
            ReflectionTestHelpers.SetPrivate(item, "Status", status);
            return item;
        }

        private static PedidoReserva BuildPedidoReserva(int idAfiliada, StatusPedidoReservaEnum status, params PedidoReservaItem[] itens)
        {
            var pedido = ReflectionTestHelpers.CreateUninitialized<PedidoReserva>();
            ReflectionTestHelpers.SetPrivate(pedido, "IdAfiliada", idAfiliada);
            ReflectionTestHelpers.SetPrivate(pedido, "Status", status);
            ReflectionTestHelpers.SetPrivate(pedido, "Itens", new List<PedidoReservaItem>(itens));
            return pedido;
        }

        private static Peca BuildPeca(int id, int idAfiliadaDoLocal, StatusExibicaoEnum status = StatusExibicaoEnum.Ativo)
        {
            var local = ReflectionTestHelpers.CreateUninitialized<Local>();
            ReflectionTestHelpers.SetPrivate(local, "IdAfiliada", idAfiliadaDoLocal);

            var peca = ReflectionTestHelpers.CreateUninitialized<Peca>();
            ReflectionTestHelpers.SetPrivate(peca, "Id", id);
            ReflectionTestHelpers.SetPrivate(peca, "Local", local);
            ReflectionTestHelpers.SetPrivate(peca, "StatusExibicao", status);
            return peca;
        }

        private static PedidoReservaRespostaValidator BuildValidator(Mock<IPecaRepository> pecaRepoMock = null)
        {
            pecaRepoMock ??= new Mock<IPecaRepository>();
            return new PedidoReservaRespostaValidator(pecaRepoMock.Object);
        }

        [Fact]
        public void Resposta_mista_valida_e_aceita_cada_item_exatamente_uma_vez()
        {
            var itens = new[] { BuildItem(1, 10, 100), BuildItem(2, 20, 200) };
            var pedido = BuildPedidoReserva(idAfiliada: 7, StatusPedidoReservaEnum.Solicitado, itens);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[]
                {
                    new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 1, Disponibilidade = StatusPedidoReservaItemEnum.Reservado },
                    new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 2, Disponibilidade = StatusPedidoReservaItemEnum.Indisponivel },
                },
            };

            var outcome = BuildValidator().Validate(pedido, request, tenantAfiliadaId: 7);

            Assert.True(outcome.IsValid);
            Assert.Equal(2, outcome.Command.Itens.Count);
            // Servidor deriva peça e período do pedido carregado — nunca do cliente.
            Assert.Contains(outcome.Command.Itens, i => i.IdItemPedidoReserva == 1 && i.IdPeca == 10 && i.IdPeriodo == 100);
            Assert.Contains(outcome.Command.Itens, i => i.IdItemPedidoReserva == 2 && i.IdPeca == 20 && i.IdPeriodo == 200);
        }

        [Fact]
        public void Payload_que_omite_item_pendente_falha_sem_montar_command()
        {
            var itens = new[] { BuildItem(1, 10, 100), BuildItem(2, 20, 200) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[] { new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 1, Disponibilidade = StatusPedidoReservaItemEnum.Reservado } },
            };

            var outcome = BuildValidator().Validate(pedido, request, 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.BadRequest, outcome.ErrorCode);
        }

        [Fact]
        public void Payload_com_item_duplicado_falha()
        {
            var itens = new[] { BuildItem(1, 10, 100) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[]
                {
                    new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 1, Disponibilidade = StatusPedidoReservaItemEnum.Reservado },
                    new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 1, Disponibilidade = StatusPedidoReservaItemEnum.Indisponivel },
                },
            };

            var outcome = BuildValidator().Validate(pedido, request, 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.BadRequest, outcome.ErrorCode);
        }

        [Fact]
        public void Payload_com_item_desconhecido_falha()
        {
            var itens = new[] { BuildItem(1, 10, 100) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[] { new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 999, Disponibilidade = StatusPedidoReservaItemEnum.Reservado } },
            };

            var outcome = BuildValidator().Validate(pedido, request, 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.BadRequest, outcome.ErrorCode);
        }

        [Fact]
        public void Enum_de_disponibilidade_diferente_de_Reservado_ou_Indisponivel_falha()
        {
            var itens = new[] { BuildItem(1, 10, 100) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[] { new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 1, Disponibilidade = StatusPedidoReservaItemEnum.Solicitado } },
            };

            var outcome = BuildValidator().Validate(pedido, request, 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.BadRequest, outcome.ErrorCode);
        }

        [Fact]
        public void Item_Reservado_com_sugestao_de_peca_falha()
        {
            var itens = new[] { BuildItem(1, 10, 100) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[]
                {
                    new PedidoReservaRespostaItemRequest
                    {
                        IdItemPedidoReserva = 1,
                        Disponibilidade = StatusPedidoReservaItemEnum.Reservado,
                        IdsPecaSugerida = new[] { 55 },
                    },
                },
            };

            var outcome = BuildValidator().Validate(pedido, request, 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.BadRequest, outcome.ErrorCode);
        }

        [Fact]
        public void Peca_sugerida_de_outro_tenant_e_rejeitada_antes_do_core()
        {
            var itens = new[] { BuildItem(1, 10, 100) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var pecaDeOutraAfiliada = BuildPeca(id: 55, idAfiliadaDoLocal: 999);
            var pecaRepoMock = new Mock<IPecaRepository>();
            pecaRepoMock.Setup(r => r.RetornaPorId(55)).Returns(pecaDeOutraAfiliada);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[]
                {
                    new PedidoReservaRespostaItemRequest
                    {
                        IdItemPedidoReserva = 1,
                        Disponibilidade = StatusPedidoReservaItemEnum.Indisponivel,
                        IdsPecaSugerida = new[] { 55 },
                    },
                },
            };

            var outcome = BuildValidator(pecaRepoMock).Validate(pedido, request, tenantAfiliadaId: 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.BadRequest, outcome.ErrorCode);
        }

        [Fact]
        public void Peca_sugerida_inexistente_e_rejeitada()
        {
            var itens = new[] { BuildItem(1, 10, 100) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var pecaRepoMock = new Mock<IPecaRepository>();
            pecaRepoMock.Setup(r => r.RetornaPorId(It.IsAny<int>())).Returns((Peca)null);

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[]
                {
                    new PedidoReservaRespostaItemRequest
                    {
                        IdItemPedidoReserva = 1,
                        Disponibilidade = StatusPedidoReservaItemEnum.Indisponivel,
                        IdsPecaSugerida = new[] { 999 },
                    },
                },
            };

            var outcome = BuildValidator(pecaRepoMock).Validate(pedido, request, 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.BadRequest, outcome.ErrorCode);
        }

        [Fact]
        public void Mais_de_uma_peca_sugerida_e_limitada_a_uma_no_command()
        {
            var itens = new[] { BuildItem(1, 10, 100) };
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Solicitado, itens);

            var pecaValida = BuildPeca(id: 55, idAfiliadaDoLocal: 7);
            var pecaRepoMock = new Mock<IPecaRepository>();
            pecaRepoMock.Setup(r => r.RetornaPorId(55)).Returns(pecaValida);
            pecaRepoMock.Setup(r => r.RetornaPorId(56)).Returns(BuildPeca(56, 7));

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[]
                {
                    new PedidoReservaRespostaItemRequest
                    {
                        IdItemPedidoReserva = 1,
                        Disponibilidade = StatusPedidoReservaItemEnum.Indisponivel,
                        IdsPecaSugerida = new[] { 55, 56 },
                    },
                },
            };

            var outcome = BuildValidator(pecaRepoMock).Validate(pedido, request, 7);

            Assert.True(outcome.IsValid);
            Assert.Single(outcome.Command.Itens.First().IdsPecaSugerida);
            Assert.Equal(55, outcome.Command.Itens.First().IdsPecaSugerida[0]);
        }

        [Fact]
        public void Pedido_de_outro_tenant_retorna_NotFound_sem_revelar_dados()
        {
            var pedido = BuildPedidoReserva(idAfiliada: 999, StatusPedidoReservaEnum.Solicitado, BuildItem(1, 10, 100));

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[] { new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 1, Disponibilidade = StatusPedidoReservaItemEnum.Reservado } },
            };

            var outcome = BuildValidator().Validate(pedido, request, tenantAfiliadaId: 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.NotFound, outcome.ErrorCode);
        }

        [Fact]
        public void Pedido_ja_respondido_gera_conflito()
        {
            var pedido = BuildPedidoReserva(7, StatusPedidoReservaEnum.Confirmado, BuildItem(1, 10, 100, StatusPedidoReservaItemEnum.Reservado));

            var request = new PedidoReservaRespostaRequest
            {
                Itens = new[] { new PedidoReservaRespostaItemRequest { IdItemPedidoReserva = 1, Disponibilidade = StatusPedidoReservaItemEnum.Reservado } },
            };

            var outcome = BuildValidator().Validate(pedido, request, 7);

            Assert.False(outcome.IsValid);
            Assert.Equal(PedidoReservaRespostaErrorCode.Conflict, outcome.ErrorCode);
        }
    }
}
