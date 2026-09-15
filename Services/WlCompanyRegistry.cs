using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Services;

public sealed record WlCompany(string Document, string LegalName, string City, string State, bool Active);
public interface IWlCompanyRegistry { Task<WlCompany> LookupAsync(string cnpj, CancellationToken ct); }

public sealed class BrasilApiCompanyRegistry : IWlCompanyRegistry
{
    private readonly HttpClient _http;
    public BrasilApiCompanyRegistry(HttpClient http) => _http = http;
    public async Task<WlCompany> LookupAsync(string cnpj, CancellationToken ct)
    {
        using var response = await _http.GetAsync(cnpj, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<CompanyDto>(cancellationToken: ct);
        if (dto == null || string.IsNullOrWhiteSpace(dto.LegalName)) throw new HttpRequestException("Resposta inválida do cadastro público.");
        return new(cnpj, dto.LegalName, dto.City, dto.State, dto.Status == 2);
    }
    private sealed class CompanyDto
    {
        [JsonPropertyName("razao_social")] public string LegalName { get; set; }
        [JsonPropertyName("municipio")] public string City { get; set; }
        [JsonPropertyName("uf")] public string State { get; set; }
        [JsonPropertyName("situacao_cadastral")] public int Status { get; set; }
    }
}
