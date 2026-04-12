namespace GP_Backend.Services.AI;

public class CandidateRankingResultDto
{
    public long CandidateId { get; set; }
    public float Score { get; set; }
    public string? Reason { get; set; }
}