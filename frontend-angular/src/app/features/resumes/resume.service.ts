import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClientService } from '../../core/api/api-client.service';
import { ApiResponse } from '../../core/models/api.model';
import {
  AtsScoreDto,
  ParsedCvResponseDto,
  ResumeDto,
  ResumeFullAnalysisRequestDto,
  ResumeFullAnalysisResponseDto,
  ResumeImprovementsResponseDto,
  ResumeTextAtsRequestDto,
  ResumeTextRequestDto
} from '../../core/models/resume.model';

@Injectable({ providedIn: 'root' })
export class ResumeService {
  private readonly base = '/resumes';

  constructor(
    private readonly http: HttpClient,
    private readonly apiClient: ApiClientService
  ) {}

  getMyResumes(): Observable<ApiResponse<ResumeDto[]>> {
    return this.apiClient.get<ResumeDto[]>(`${this.base}/my-resumes`);
  }

  getResume(resumeId: number): Observable<ApiResponse<ResumeDto>> {
    return this.apiClient.get<ResumeDto>(`${this.base}/${resumeId}`);
  }

  uploadResume(file: File, isDefault = false, parseNow = true): Observable<ApiResponse<ResumeDto>> {
    const formData = new FormData();
    formData.append('file', file);

    return this.apiClient.post<ResumeDto>(this.base, formData, { isDefault, parseNow });
  }

  deleteResume(resumeId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.delete<unknown>(`${this.base}/${resumeId}`);
  }

  setDefaultResume(resumeId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${resumeId}/set-default`, {});
  }

  parseStoredResume(resumeId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${resumeId}/parse`, {});
  }

  getStoredResumeAtsScore(resumeId: number, jobId?: number): Observable<ApiResponse<AtsScoreDto>> {
    return this.apiClient.get<AtsScoreDto>(`${this.base}/${resumeId}/ats-score`, { jobId });
  }

  parseResumeText(payload: ResumeTextRequestDto): Observable<ApiResponse<ParsedCvResponseDto>> {
    return this.apiClient.post<ParsedCvResponseDto>(`${this.base}/parse-text`, payload);
  }

  getAtsScoreFromText(payload: ResumeTextAtsRequestDto): Observable<ApiResponse<AtsScoreDto>> {
    return this.apiClient.post<AtsScoreDto>(`${this.base}/ats-score-text`, payload);
  }

  getImprovements(payload: ResumeTextRequestDto): Observable<ApiResponse<ResumeImprovementsResponseDto>> {
    return this.apiClient.post<ResumeImprovementsResponseDto>(`${this.base}/improvements`, payload);
  }

  getFullAnalysis(payload: ResumeFullAnalysisRequestDto): Observable<ApiResponse<ResumeFullAnalysisResponseDto>> {
    return this.apiClient.post<ResumeFullAnalysisResponseDto>(`${this.base}/full-analysis`, payload);
  }

  downloadResume(resumeId: number): Observable<Blob> {
    return this.http.get(`${this.base}/${resumeId}/download`, {
      responseType: 'blob'
    });
  }
}
