using Microsoft.AspNetCore.Http;

namespace Veiculando.WhiteLabel.Api.Services
{
    public interface IFileValidationService
    {
        bool IsValidFile(IFormFile file, long maxSizeBytes, out string errorMessage);
        string SanitizeFileName(string originalFileName);
    }
}
