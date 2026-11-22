namespace JobLens.Api.Compatibility;

internal static class MediaUrls
{
    public static string? CandidateImage(long candidateId, string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        return $"/api/candidates/{candidateId}/image?v={Version(storedPath)}";
    }

    public static string? CompanyLogo(long? companyId, string? storedLogo)
    {
        if (string.IsNullOrWhiteSpace(storedLogo))
        {
            return null;
        }

        if (storedLogo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            storedLogo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return storedLogo;
        }

        return companyId.HasValue ? $"/api/companies/{companyId.Value}/logo?v={Version(storedLogo)}" : null;
    }

    private static string Version(string storedPath)
    {
        var name = Path.GetFileName(storedPath.Replace('\\', '/'));
        return name.Length > 8 ? name[..8] : name;
    }
}
