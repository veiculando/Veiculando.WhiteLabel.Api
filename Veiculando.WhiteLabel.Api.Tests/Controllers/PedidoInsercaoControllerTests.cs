using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.TestHelpers;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests.Controllers
{
    public class PedidoInsercaoControllerTests
    {
        private readonly Mock<IPedidoInsercaoRepository> _repoMock = new();
        private readonly Mock<IFileServerClient> _fileServerMock = new();
        private readonly Mock<ITenantContext> _tenantMock = new();

        public PedidoInsercaoControllerTests()
        {
            _tenantMock.SetupGet(t => t.AfiliadaId).Returns(7);
        }

        private static PedidoInsercao BuildPi(int idAfiliada, string codigo)
        {
            var pi = ReflectionTestHelpers.CreateUninitialized<PedidoInsercao>();
            ReflectionTestHelpers.SetPrivate(pi, "IdAfiliada", idAfiliada);
            ReflectionTestHelpers.SetPrivate(pi, "Codigo", codigo);
            return pi;
        }

        private PedidoInsercaoController BuildController() =>
            new(_repoMock.Object, _fileServerMock.Object, _tenantMock.Object, null);

        [Fact]
        public async Task Pdf_inexistente_retorna_404_sem_chamar_o_FileServer()
        {
            _repoMock.Setup(r => r.RetornaPIPorCodigo("XYZ")).Returns((PedidoInsercao)null);

            var result = await BuildController().Pdf("XYZ", CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
            _fileServerMock.Verify(f => f.GetPedidoInsercaoPdfAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Pdf_de_outro_tenant_retorna_404_sem_chamar_o_FileServer()
        {
            var pi = BuildPi(idAfiliada: 999, codigo: "ABC");
            _repoMock.Setup(r => r.RetornaPIPorCodigo("ABC")).Returns(pi);

            var result = await BuildController().Pdf("ABC", CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
            _fileServerMock.Verify(f => f.GetPedidoInsercaoPdfAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Pdf_da_propria_afiliada_e_entregue_como_application_pdf_sem_expor_url_interna()
        {
            var pi = BuildPi(idAfiliada: 7, codigo: "ABC");
            _repoMock.Setup(r => r.RetornaPIPorCodigo("ABC")).Returns(pi);
            _fileServerMock
                .Setup(f => f.GetPedidoInsercaoPdfAsync("ABC", It.IsAny<CancellationToken>()))
                .ReturnsAsync(FileServerPdfResult.Ok(new byte[] { 1, 2, 3 }, "application/pdf"));

            var result = await BuildController().Pdf("ABC", CancellationToken.None);

            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("application/pdf", fileResult.ContentType);
            Assert.Equal(new byte[] { 1, 2, 3 }, fileResult.FileContents);
            // Nome de arquivo derivado do código do PI, não de uma URL do FileServer.
            Assert.Contains("ABC", fileResult.FileDownloadName);
        }

        [Fact]
        public async Task Falha_do_FileServer_e_mapeada_com_seguranca_sem_expor_detalhe_interno()
        {
            var pi = BuildPi(idAfiliada: 7, codigo: "ABC");
            _repoMock.Setup(r => r.RetornaPIPorCodigo("ABC")).Returns(pi);
            _fileServerMock
                .Setup(f => f.GetPedidoInsercaoPdfAsync("ABC", It.IsAny<CancellationToken>()))
                .ReturnsAsync(FileServerPdfResult.Error());

            var result = await BuildController().Pdf("ABC", CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(502, statusResult.StatusCode);
        }
    }
}
