using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure;

public sealed class FakeWlUploadStorage : IWlUploadStorage
{
    public ConcurrentDictionary<string, (WlStoredFile File, byte[] Bytes, bool Pending)> Files { get; } = new();
    public bool FailPut { get; set; }
    public bool FailDelete { get; set; }
    public bool FailCommit { get; set; }
    public async Task PutAsync(WlStoredFile file, Stream body, int actorId, CancellationToken ct)
    {
        if (FailPut) throw new IOException("Storage indisponível");
        using var bytes = new MemoryStream();
        await body.CopyToAsync(bytes, ct);
        Files[file.Key] = (file, bytes.ToArray(), true);
    }
    public Task<WlStoredFile> InfoAsync(string key, CancellationToken ct) => Task.FromResult(Files[key].File);
    public Task<Stream> ReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Files[key].Bytes));
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (FailDelete) throw new IOException("Falha temporária de limpeza");
        Files.TryRemove(key, out _);
        return Task.CompletedTask;
    }
    public Task CommitAsync(string key, CancellationToken ct)
    {
        if (FailCommit) throw new IOException("Falha temporária de marcação");
        var stored = Files[key];
        Files[key] = (stored.File, stored.Bytes, false);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<string>> PendingAsync(DateTimeOffset before, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Files.Values.Where(x => x.Pending && x.File.CreatedAt < before).Select(x => x.File.Key).ToArray());
}
