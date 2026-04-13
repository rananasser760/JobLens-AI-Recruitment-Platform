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

export interface ScrapingStatusDto {
  running?: boolean;
  lastStatus?: string;
  updatedAt?: string | null;
  phase?: string;
  message?: string;
  progressPercent?: number;
  processedJobs?: number;
  totalJobs?: number;
  insertedJobs?: number;
  updatedJobs?: number;
  processedCategories?: number;
  upsertedJobs?: number;
  requestedMaxCategories?: number;
}

export interface ScrapingCoverageDto {
  applyLinkCoveragePct?: number;
  externalApplyLinkCoveragePct?: number;
  egyptOnlyRatioPct?: number;
}

export interface ScrapingFillRatesDto {
  descriptionPct?: number;
  requirementsPct?: number;
  responsibilitiesPct?: number;
  skillsPct?: number;
  employmentTypePct?: number;
  experienceLevelPct?: number;
  cityPct?: number;
  countryPct?: number;
}

export interface ScrapingEnrichmentDto {
  withTagPct?: number;
  llmPct?: number;
}

export interface ScrapingRuntimeDto {
  running?: boolean;
  status?: string;
  updatedAtUtc?: string | null;
  error?: string | null;
}

export interface NonEgyptLocationDto {
  location: string;
  count: number;
}

export interface ScrapingDiagnosticsDto {
  generatedAtUtc?: string;
  totalExternalJobs?: number;
  coverage?: ScrapingCoverageDto;
  fillRates?: ScrapingFillRatesDto;
  enrichment?: ScrapingEnrichmentDto;
  scrapeStats?: ScrapingRuntimeDto;
  topNonEgyptLocations?: NonEgyptLocationDto[];
}
