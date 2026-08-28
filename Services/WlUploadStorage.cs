using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Configuration;

namespace Veiculando.WhiteLabel.Api.Services;

public sealed record WlStoredFile(string Key, string FileName, string ContentType, long Size, string Sha256, DateTimeOffset CreatedAt);

public interface IWlUploadStorage
{
    Task PutAsync(WlStoredFile file, Stream content, int actorId, CancellationToken ct);
    Task<WlStoredFile> InfoAsync(string key, CancellationToken ct);
    Task<Stream> ReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task CommitAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<string>> PendingAsync(DateTimeOffset before, CancellationToken ct);
}

/// <summary>Container exclusivo, privado; nenhuma URL SAS ou chave é devolvida à UI.</summary>
public sealed class AzureWlUploadStorage : IWlUploadStorage
{
    private readonly IConfiguration _config;
    public AzureWlUploadStorage(IConfiguration config) => _config = config;

    private async Task<CloudBlobContainer> ContainerAsync(CancellationToken ct)
    {
        var connection = _config.GetConnectionString("BlobStorageConnStr");
        var name = _config["WlUploads:Container"];
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(name) || !name.StartsWith("wl-uploads-", StringComparison.Ordinal))
            throw new InvalidOperationException("Configure o storage privado de uploads WhiteLabel.");
        var account = CloudStorageAccount.Parse(connection);
        if (account.BlobEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("O storage de uploads requer HTTPS.");
        var container = account.CreateCloudBlobClient().GetContainerReference(name);
        await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Off, null, null, ct);
        var permissions = await container.GetPermissionsAsync(null, null, null, ct);
        if (permissions.PublicAccess != BlobContainerPublicAccessType.Off)
            throw new InvalidOperationException("Uploads WhiteLabel não podem usar container público.");
        return container;
    }

    private async Task<CloudBlockBlob> BlobAsync(string key, CancellationToken ct)
    {
        WlUploadKey.Parse(key); // namespace fechado, inclusive para a reconciliação.
        return (await ContainerAsync(ct)).GetBlockBlobReference(key);
    }

    public async Task PutAsync(WlStoredFile file, Stream content, int actorId, CancellationToken ct)
    {
        var blob = await BlobAsync(file.Key, ct);
        blob.Properties.ContentType = file.ContentType;
        blob.Metadata["sha256"] = file.Sha256;
        blob.Metadata["createdAt"] = file.CreatedAt.ToString("O");
        blob.Metadata["wlActorId"] = actorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Persistido atomicamente com o blob: sobrevive a falha do banco ou reinício do BFF.
        blob.Metadata["state"] = "pending";
        await blob.UploadFromStreamAsync(content, AccessCondition.GenerateIfNoneMatchCondition("*"),
            new BlobRequestOptions { StoreBlobContentMD5 = true }, null, ct);
    }

    public async Task<WlStoredFile> InfoAsync(string key, CancellationToken ct)
    {
        var blob = await BlobAsync(key, ct);
        await blob.FetchAttributesAsync(null, null, null, ct);
        return new WlStoredFile(key, WlUploadKey.Parse(key).FileName, blob.Properties.ContentType,
            blob.Properties.Length, blob.Metadata["sha256"], DateTimeOffset.Parse(blob.Metadata["createdAt"]));
    }

    public async Task<Stream> ReadAsync(string key, CancellationToken ct)
    {
        var blob = await BlobAsync(key, ct);
        return await blob.OpenReadAsync(null, null, null, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await (await BlobAsync(key, ct)).DeleteIfExistsAsync(DeleteSnapshotsOption.None, null, null, null, ct);

    public async Task CommitAsync(string key, CancellationToken ct)
    {
        var blob = await BlobAsync(key, ct);
        await blob.FetchAttributesAsync(null, null, null, ct);
        blob.Metadata["state"] = "committed";
        await blob.SetMetadataAsync(AccessCondition.GenerateIfMatchCondition(blob.Properties.ETag), null, null, ct);
    }

    public async Task<IReadOnlyList<string>> PendingAsync(DateTimeOffset before, CancellationToken ct)
    {
        var container = await ContainerAsync(ct);
        var pending = new List<string>();
        BlobContinuationToken continuation = null;
        do
        {
            var page = await container.ListBlobsSegmentedAsync("tenant-", true, BlobListingDetails.Metadata,
                100, continuation, null, null, ct);
            pending.AddRange(page.Results.OfType<CloudBlockBlob>()
                .Where(b => b.Metadata.TryGetValue("state", out var state) && state == "pending"
                    && b.Metadata.TryGetValue("createdAt", out var created) && DateTimeOffset.TryParse(created, out var date) && date < before)
                .Select(b => b.Name));
            continuation = page.ContinuationToken;
        } while (continuation != null);
        return pending;
    }
}

public sealed record WlUploadKey(int TenantId, string Kind, int ResourceId, string FileName)
{
    public static WlUploadKey Parse(string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(key ?? "",
            @"^tenant-([1-9][0-9]*)/(pecas|checking)/([1-9][0-9]*)/(wl-[a-f0-9]{32}\.(?:jpg|png))$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var tenant) || !int.TryParse(match.Groups[3].Value, out var resource))
            throw new ArgumentException("Referência de upload inválida.");
        return new(tenant, match.Groups[2].Value, resource, match.Groups[4].Value);
    }
}
