using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Veiculando.Domain.Commands.Handlers.Pedidos;
using Veiculando.Domain.Commands.Inputs.Pedidos;
using Veiculando.Domain.Commands.Results.Pedidos;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Contracts.PedidosReserva;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.TestHelpers;
using Veiculando.WhiteLabel.Api.Validation;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests.Controllers
{
    public class PedidoReservaControllerTests
    {
        private readonly Mock<IPedidoReservaRepository> _pedidoReservaRepoMock = new();
        private readonly Mock<IPedidoReservaRespostaValidator> _validatorMock = new();
        private readonly Mock<ITenantContext> _tenantMock = new();
        private readonly Mock<IServiceAccountResolver> _serviceAccountMock = new();

        public PedidoReservaControllerTests()
        {
            _tenantMock.SetupGet(t => t.AfiliadaId).Returns(7);
        }

        private static PedidoReserva BuildPedido(int idAfiliada, StatusPedidoReservaEnum status = StatusPedidoReservaEnum.Solicitado)
        {
            var pedido = ReflectionTestHelpers.CreateUninitialized<PedidoReserva>();
            ReflectionTestHelpers.SetPrivate(pedido, "IdAfiliada", idAfiliada);
            ReflectionTestHelpers.SetPrivate(pedido, "Status", status);
            ReflectionTestHelpers.SetPrivate(pedido, "Itens", new List<PedidoReservaItem>());
            return pedido;
        }

        [Fact]
        public void Pedido_inexistente_retorna_404()
        {
            _pedidoReservaRepoMock.Setup(r => r.RetornaPorCodigo("XYZ")).Returns((PedidoReserva)null);

            var controller = new PedidoReservaController(
                null, _pedidoReservaRepoMock.Object, _validatorMock.Object, _tenantMock.Object, _serviceAccountMock.Object);

            var result = controller.Responder("XYZ", new PedidoReservaRespostaRequest());

            Assert.IsType<NotFoundResult>(result);
            _validatorMock.Verify(v => v.Validate(It.IsAny<PedidoReserva>(), It.IsAny<PedidoReservaRespostaRequest>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public void Pedido_de_outro_tenant_retorna_404_sem_chamar_validador_nem_core()
        {
            var pedido = BuildPedido(idAfiliada: 999);
            _pedidoReservaRepoMock.Setup(r => r.RetornaPorCodigo("ABC")).Returns(pedido);

            var controller = new PedidoReservaController(
                null, _pedidoReservaRepoMock.Object, _validatorMock.Object, _tenantMock.Object, _serviceAccountMock.Object);

            var result = controller.Responder("ABC", new PedidoReservaRespostaRequest());

            Assert.IsType<NotFoundResult>(result);
            _validatorMock.Verify(v => v.Validate(It.IsAny<PedidoReserva>(), It.IsAny<PedidoReservaRespostaRequest>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public void Falha_de_validacao_nunca_chega_ao_handler_do_core()
        {
            var pedido = BuildPedido(idAfiliada: 7);
            _pedidoReservaRepoMock.Setup(r => r.RetornaPorCodigo("ABC")).Returns(pedido);
            _validatorMock
                .Setup(v => v.Validate(pedido, It.IsAny<PedidoReservaRespostaRequest>(), 7))
                .Returns(PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "payload inválido"));

            // handler = null e o teste passa: se o controller tentasse chamar
            // _handler.Handle(...) aqui, lançaria NullReferenceException. A
            // validação precisa interceptar ANTES.
            var controller = new PedidoReservaController(
                null, _pedidoReservaRepoMock.Object, _validatorMock.Object, _tenantMock.Object, _serviceAccountMock.Object);

            var result = controller.Responder("ABC", new PedidoReservaRespostaRequest());

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            _serviceAccountMock.Verify(s => s.Resolve(), Times.Never);
        }

        [Fact]
        public void Conflito_de_validacao_retorna_409()
        {
            var pedido = BuildPedido(idAfiliada: 7, StatusPedidoReservaEnum.Confirmado);
            _pedidoReservaRepoMock.Setup(r => r.RetornaPorCodigo("ABC")).Returns(pedido);
            _validatorMock
                .Setup(v => v.Validate(pedido, It.IsAny<PedidoReservaRespostaRequest>(), 7))
                .Returns(PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.Conflict, "já respondido"));

            var controller = new PedidoReservaController(
                null, _pedidoReservaRepoMock.Object, _validatorMock.Object, _tenantMock.Object, _serviceAccountMock.Object);

            var result = controller.Responder("ABC", new PedidoReservaRespostaRequest());

            Assert.IsType<ConflictObjectResult>(result);
        }
    }
}
