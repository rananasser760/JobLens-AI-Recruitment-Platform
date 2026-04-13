import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClientService } from '../../core/api/api-client.service';
import { ApiResponse, PaginatedResponse } from '../../core/models/api.model';
import {
  ApplicationDto,
  ApplicationSearchParams,
  ApplyToJobDto,
  BulkUpdateApplicationStatusDto,
  BulkUpdateApplicationStatusResultDto,
  CandidateApplicationDto,
  UpdateApplicationStatusDto
} from '../../core/models/application.model';

@Injectable({ providedIn: 'root' })
export class ApplicationsService {
  private readonly base = '/applications';

  constructor(private readonly apiClient: ApiClientService) {}

  getById(applicationId: number): Observable<ApiResponse<ApplicationDto>> {
    return this.apiClient.get<ApplicationDto>(`${this.base}/${applicationId}`);
  }

  apply(dto: ApplyToJobDto): Observable<ApiResponse<ApplicationDto>> {
    return this.apiClient.post<ApplicationDto>(this.base, dto);
  }

  updateStatus(applicationId: number, dto: UpdateApplicationStatusDto): Observable<ApiResponse<unknown>> {
    return this.apiClient.put<unknown>(`${this.base}/${applicationId}/status`, dto);
  }

  bulkUpdateStatus(dto: BulkUpdateApplicationStatusDto): Observable<ApiResponse<BulkUpdateApplicationStatusResultDto>> {
    return this.apiClient.put<BulkUpdateApplicationStatusResultDto>(`${this.base}/bulk-status`, dto);
  }

  withdraw(applicationId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${applicationId}/withdraw`, {});
  }

  getMyApplications(params: ApplicationSearchParams): Observable<ApiResponse<PaginatedResponse<ApplicationDto>>> {
    return this.apiClient.get<PaginatedResponse<ApplicationDto>>(`${this.base}/my-applications`, params as Record<string, unknown>);
  }

  getJobApplications(jobId: number, params: ApplicationSearchParams): Observable<ApiResponse<PaginatedResponse<CandidateApplicationDto>>> {
    return this.apiClient.get<PaginatedResponse<CandidateApplicationDto>>(`${this.base}/job/${jobId}`, params as Record<string, unknown>);
  }

  getRankedCandidates(jobId: number): Observable<ApiResponse<CandidateApplicationDto[]>> {
    return this.apiClient.get<CandidateApplicationDto[]>(`${this.base}/job/${jobId}/ranked`);
  }

  checkIfApplied(jobId: number): Observable<ApiResponse<{ hasApplied: boolean }>> {
    return this.apiClient.get<{ hasApplied: boolean }>(`${this.base}/check/${jobId}`);
  }
}
