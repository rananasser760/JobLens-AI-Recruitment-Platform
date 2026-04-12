namespace GP_Backend.Models.DTOs.Common;

public class EnumOptionDto
{
    public int Value { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class EnumMetadataDto
{
    public Dictionary<string, List<EnumOptionDto>> Enums { get; set; } = new();
}
