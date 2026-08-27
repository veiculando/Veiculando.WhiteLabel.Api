using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

public class UploadPipelineTests
{
    private static readonly WlStoredFile File = new("tenant-1/pecas/2/0123456789abcdef0123456789abcdef.jpg", "0123456789abcdef0123456789abcdef.jpg", "image/jpeg", 3, "abc", DateTimeOffset.UtcNow.AddDays(-2));

    [Fact]
    public async Task Confirma_blob_antes_de_gravar_referencia()
    {
        var store = new FakeWlUploadStorage();
        var pipeline = new WlUploadPipeline(store, NullLogger<WlUploadPipeline>.Instance);
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });
        await pipeline.SaveAsync(File, body, 3, () => { store.Files.Should().ContainKey(File.Key); return Task.CompletedTask; }, () => Task.FromResult(false), default);
        store.Files[File.Key].Pending.Should().BeFalse();
        (await store.ReadAsync(File.Key, default)).Length.Should().Be(3);
    }

    [Fact]
    public async Task Falha_no_blob_nao_chama_banco()
    {
        var store = new FakeWlUploadStorage { FailPut = true };
        var called = false;
        var pipeline = new WlUploadPipeline(store, NullLogger<WlUploadPipeline>.Instance);
        using var body = new MemoryStream();
        Func<Task> act = () => pipeline.SaveAsync(File, body, 3, () => { called = true; return Task.CompletedTask; }, () => Task.FromResult(false), default);
        await act.Should().ThrowAsync<IOException>();
        called.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    public async Task Falha_no_banco_compensa_ou_deixa_pendente_observavel_sem_apagar_referencia(bool failDelete, bool referenced, int remaining)
    {
        var store = new FakeWlUploadStorage { FailDelete = failDelete };
        var pipeline = new WlUploadPipeline(store, NullLogger<WlUploadPipeline>.Instance);
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });
        Func<Task> act = () => pipeline.SaveAsync(File, body, 3, () => throw new IOException("Banco falhou"), () => Task.FromResult(referenced), default);
        await act.Should().ThrowAsync<IOException>();
        store.Files.Count.Should().Be(remaining);
        if (remaining == 1) (await store.PendingAsync(DateTimeOffset.UtcNow.AddDays(-1), default)).Should().Contain(File.Key);
    }

    [Fact]
    public async Task Cancelamento_nao_confirma_upload()
    {
        var store = new FakeWlUploadStorage();
        var pipeline = new WlUploadPipeline(store, NullLogger<WlUploadPipeline>.Instance);
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Func<Task> act = () => pipeline.SaveAsync(File, body, 3, () => Task.CompletedTask, () => Task.FromResult(false), cancel.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Files.Should().BeEmpty();
    }

    [Theory]
    [InlineData("tenant-1/pecas/2/../../foto.jpg")]
    [InlineData("https://storage/arquivo.jpg")]
    [InlineData("tenant-0/pecas/2/0123456789abcdef0123456789abcdef.jpg")]
    public void Referencia_recusa_caminhos_livres(string key)
    {
        Action act = () => WlUploadKey.Parse(key);
        act.Should().Throw<ArgumentException>();
    }
}
