import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { ApiClientService } from '../../../core/api/api-client.service';
import { ApiResponse } from '../../../core/models/api.model';
import {
  BackgroundJobDto,
  CleanupJobsRequest,
  TriggerScrapeRequest
} from '../models/admin.model';

@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly base = '/admin';

  constructor(private readonly apiClient: ApiClientService) {}

  triggerScrape(payload: TriggerScrapeRequest): Observable<ApiResponse<boolean>> {
    return this.apiClient.post<boolean>(`${this.base}/scraping/trigger`, payload);
  }

  cleanupJobs(payload: CleanupJobsRequest): Observable<ApiResponse<boolean>> {
    return this.apiClient.post<boolean>(`${this.base}/jobs/cleanup`, payload);
  }

  refreshRecommendations(): Observable<ApiResponse<boolean>> {
    return this.apiClient.post<boolean>(`${this.base}/recommendations/refresh`, {});
  }

  getBackgroundJobs(): Observable<ApiResponse<BackgroundJobDto[]>> {
    return this.apiClient.get<BackgroundJobDto[]>(`${this.base}/background-jobs`);
  }
}
