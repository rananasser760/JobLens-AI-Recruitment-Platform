import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { ApiClientService } from '../../../core/api/api-client.service';
import { ApiResponse } from '../../../core/models/api.model';
import {
  BackgroundJobDto,
  CleanupJobsRequest,
  ScrapingDiagnosticsDto,
  ScrapingStatusDto,
  TriggerScrapeRequest,
} from '../models/admin.model';
import { ScrapedJobDto } from '../../../core/models/job.model';

@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly apiClient = inject(ApiClientService);

  private readonly base = '/admin';
  private readonly jobsBase = '/jobs';

  triggerScrape(payload: TriggerScrapeRequest): Observable<ApiResponse<boolean>> {
    return this.apiClient.post<boolean>(`${this.base}/scrape`, payload);
  }

  cleanupJobs(payload: CleanupJobsRequest): Observable<ApiResponse<boolean>> {
    return this.apiClient.post<boolean>(`${this.base}/cleanup`, payload);
  }

  refreshRecommendations(): Observable<ApiResponse<boolean>> {
    return this.apiClient.post<boolean>(`${this.base}/recommendations/refresh`, {});
  }

  getBackgroundJobs(): Observable<ApiResponse<BackgroundJobDto[]>> {
    return this.apiClient.get<BackgroundJobDto[]>(`${this.base}/jobs`);
  }

  getScrapingStatus(): Observable<ApiResponse<ScrapingStatusDto>> {
    return this.apiClient.get<ScrapingStatusDto>(`${this.jobsBase}/scraping/status`);
  }

  getScrapingDiagnostics(): Observable<ApiResponse<ScrapingDiagnosticsDto>> {
    return this.apiClient.get<ScrapingDiagnosticsDto>(`${this.jobsBase}/scraping/diagnostics`);
  }

  getScrapedJobs(limit = 12): Observable<ApiResponse<ScrapedJobDto[]>> {
    return this.apiClient.get<ScrapedJobDto[]>(`${this.jobsBase}/scraping/jobs`, { limit });
  }
}
