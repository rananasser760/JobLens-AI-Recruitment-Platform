export interface TriggerScrapeRequest {
  maxCategories?: number | null;
}

export interface CleanupJobsRequest {
  staleAfterDays: number;
}

export interface BackgroundJobDto {
  jobId: number;
  jobType: string;
  status: string;
  attempts: number;
  lastRunAtUtc?: string | null;
  nextRunAtUtc?: string | null;
  error: string;
}
