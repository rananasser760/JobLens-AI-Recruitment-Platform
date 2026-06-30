import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { timeout } from 'rxjs/operators';
import { ApiClientService } from '../../core/api/api-client.service';
import { ApiResponse, PaginatedResponse } from '../../core/models/api.model';
import {
  CandidateRecommendationDto,
  CreateJobDto,
  CreateJobSkillDto,
  JobOperationResultDto,
  JobDto,
  JobListDto,
  JobRecommendationDto,
  JobSearchParams,
  MatchJobsFromTextRequestDto,
  RecruitmentStatusDto,
  ScrapingStatusDto,
  ScrapedJobDto,
  UpdateJobDto
} from '../../core/models/job.model';

@Injectable({ providedIn: 'root' })
export class JobsService {
  private readonly base = '/jobs';

  constructor(private readonly apiClient: ApiClientService) {}

  search(params: JobSearchParams): Observable<ApiResponse<PaginatedResponse<JobListDto>>> {
    return this.apiClient.get<PaginatedResponse<JobListDto>>(this.base, params as Record<string, unknown>);
  }

  getById(jobId: number): Observable<ApiResponse<JobDto>> {
    return this.apiClient.get<JobDto>(`${this.base}/${jobId}`);
  }

  create(dto: CreateJobDto): Observable<ApiResponse<JobDto>> {
    return this.apiClient.post<JobDto>(this.base, dto);
  }

  update(jobId: number, dto: UpdateJobDto): Observable<ApiResponse<JobDto>> {
    return this.apiClient.put<JobDto>(`${this.base}/${jobId}`, dto);
  }

  delete(jobId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.delete<unknown>(`${this.base}/${jobId}`);
  }

  toggleStatus(jobId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${jobId}/toggle-status`, {});
  }

  addSkill(jobId: number, dto: CreateJobSkillDto): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${jobId}/skills`, dto);
  }

  removeSkill(jobId: number, skillId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.delete<unknown>(`${this.base}/${jobId}/skills/${skillId}`);
  }

  getCandidateRecommendations(limit = 10, forceRefresh = false): Observable<ApiResponse<JobRecommendationDto[]>> {
    return this.apiClient
      .get<JobRecommendationDto[]>(`${this.base}/recommendations`, {
        limit,
        forceRefresh: forceRefresh || undefined
      })
      .pipe(timeout({ first: forceRefresh ? 30000 : 8000 }));
  }

  getCandidateRecommendationsForJob(jobId: number, limit = 10, forceRefresh = false): Observable<ApiResponse<CandidateRecommendationDto[]>> {
    return this.apiClient
      .get<CandidateRecommendationDto[]>(`${this.base}/${jobId}/candidate-recommendations`, {
        limit,
        forceRefresh: forceRefresh || undefined
      })
      .pipe(timeout({ first: forceRefresh ? 30000 : 8000 }));
  }

  matchFromText(payload: MatchJobsFromTextRequestDto, limit = 5): Observable<ApiResponse<JobRecommendationDto[]>> {
    return this.apiClient.post<JobRecommendationDto[]>(
      `${this.base}/recommendations/match-from-text`,
      payload,
      { limit }
    );
  }

  getScrapedJobs(keyword?: string, location?: string, limit = 50): Observable<ApiResponse<ScrapedJobDto[]>> {
    return this.apiClient.get<ScrapedJobDto[]>(`${this.base}/scraping/jobs`, {
      limit,
      keyword,
      location
    });
  }

  getScrapingStatus(): Observable<ApiResponse<ScrapingStatusDto>> {
    return this.apiClient.get<ScrapingStatusDto>(`${this.base}/scraping/status`);
  }

  triggerScraping(maxCategories?: number): Observable<ApiResponse<JobOperationResultDto>> {
    return this.apiClient.post<JobOperationResultDto>(`${this.base}/scraping/trigger`, {}, {
      maxCategories
    });
  }

  getRecruitmentStatus(): Observable<ApiResponse<RecruitmentStatusDto>> {
    return this.apiClient.get<RecruitmentStatusDto>(`${this.base}/recruitment/status`);
  }

  getMyJobs(params: JobSearchParams): Observable<ApiResponse<PaginatedResponse<JobListDto>>> {
    return this.apiClient.get<PaginatedResponse<JobListDto>>(`${this.base}/my-jobs`, params as Record<string, unknown>);
  }
}
