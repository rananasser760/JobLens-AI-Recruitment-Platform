import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClientService } from '../../core/api/api-client.service';
import { ApiResponse } from '../../core/models/api.model';
import {
  RecruiterDashboardDto,
  RecruiterProfileDto,
  UpdateRecruiterProfileDto
} from '../../core/models/recruiter.model';

@Injectable({ providedIn: 'root' })
export class RecruiterService {
  private readonly base = '/recruiters';

  constructor(private readonly apiClient: ApiClientService) {}

  getProfile(): Observable<ApiResponse<RecruiterProfileDto>> {
    return this.apiClient.get<RecruiterProfileDto>(`${this.base}/profile`);
  }

  updateProfile(dto: UpdateRecruiterProfileDto): Observable<ApiResponse<RecruiterProfileDto>> {
    return this.apiClient.put<RecruiterProfileDto>(`${this.base}/profile`, dto);
  }

  getDashboard(): Observable<ApiResponse<RecruiterDashboardDto>> {
    return this.apiClient.get<RecruiterDashboardDto>(`${this.base}/dashboard`);
  }
}
