namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>Dados apresentados ao revisor; persistidos apenas no rascunho KYC.</summary>
public sealed class AppKycBusinessData
{
    public string TradeName { get; set; }
    public string LegalName { get; set; }
    public string Phone { get; set; }
    public string Email { get; set; }
    public string Website { get; set; }
    public string Street { get; set; }
    public string Number { get; set; }
    public string District { get; set; }
    public string Complement { get; set; }
    public string ZipCode { get; set; }
    public string City { get; set; }
    public string State { get; set; }
    public string StateTaxId { get; set; }
    public string MunicipalTaxId { get; set; }
    public string RepresentativeName { get; set; }
    public string RepresentativeCpf { get; set; }
    public string RepresentativeEmail { get; set; }
    public string RepresentativePhone { get; set; }
}
