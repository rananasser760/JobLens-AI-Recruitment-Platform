using System.Security.Cryptography;
using System.Text;
using JobLens.Application.Interfaces;

namespace JobLens.Infrastructure.Security;

public sealed class ContentHashService : IContentHashService
{
    public string Compute(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public string Compute(string content) => Compute(Encoding.UTF8.GetBytes(content));
}
