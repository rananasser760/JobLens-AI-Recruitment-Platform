import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClientService } from '../../core/api/api-client.service';
import { ApiResponse, PaginatedResponse } from '../../core/models/api.model';
import {
  AddSkillDto,
  CandidateDashboardDto,
  CandidateListDto,
  CandidateProfileDto,
  CandidateSearchParams,
  CandidateSkillDto,
  FillProfileFromResumeResultDto,
  UpdateCandidateProfileDto
} from '../../core/models/candidate.model';

@Injectable({ providedIn: 'root' })
export class CandidateService {
  private readonly base = '/candidates';

  constructor(private readonly apiClient: ApiClientService) {}

  getProfile(): Observable<ApiResponse<CandidateProfileDto>> {
    return this.apiClient.get<CandidateProfileDto>(`${this.base}/profile`);
  }

  getDashboard(): Observable<ApiResponse<CandidateDashboardDto>> {
    return this.apiClient.get<CandidateDashboardDto>(`${this.base}/dashboard`);
  }

  getById(candidateId: number): Observable<ApiResponse<CandidateProfileDto>> {
    return this.apiClient.get<CandidateProfileDto>(`${this.base}/${candidateId}`);
  }

  updateProfile(dto: UpdateCandidateProfileDto): Observable<ApiResponse<CandidateProfileDto>> {
    return this.apiClient.put<CandidateProfileDto>(`${this.base}/profile`, dto);
  }

  updateProfileImage(file: File): Observable<ApiResponse<unknown>> {
    const form = new FormData();
    form.append('file', file);
    return this.apiClient.post<unknown>(`${this.base}/profile/image`, form);
  }

  getSkills(candidateId: number): Observable<ApiResponse<CandidateSkillDto[]>> {
    return this.apiClient.get<CandidateSkillDto[]>(`${this.base}/${candidateId}/skills`);
  }

  addSkill(dto: AddSkillDto): Observable<ApiResponse<CandidateSkillDto>> {
    return this.apiClient.post<CandidateSkillDto>(`${this.base}/skills`, dto);
  }

  removeSkill(skillId: number): Observable<ApiResponse<unknown>> {
    return this.apiClient.delete<unknown>(`${this.base}/skills/${skillId}`);
  }

  search(params: CandidateSearchParams): Observable<ApiResponse<PaginatedResponse<CandidateListDto>>> {
    return this.apiClient.get<PaginatedResponse<CandidateListDto>>(`${this.base}/search`, params as Record<string, unknown>);
  }

  fillFromResume(resumeId: number): Observable<ApiResponse<FillProfileFromResumeResultDto>> {
    return this.apiClient.post<FillProfileFromResumeResultDto>(`${this.base}/fill-from-resume/${resumeId}`, {});
  }
}
