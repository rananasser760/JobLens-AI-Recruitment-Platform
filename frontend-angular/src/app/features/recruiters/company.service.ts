import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClientService } from '../../core/api/api-client.service';
import { ApiResponse } from '../../core/models/api.model';
import {
  CompanyDto,
  CreateCompanyDto,
  UpdateCompanyDto
} from '../../core/models/recruiter.model';

@Injectable({ providedIn: 'root' })
export class CompanyService {
  private readonly base = '/companies';

  constructor(private readonly apiClient: ApiClientService) {}

  getAll(): Observable<ApiResponse<CompanyDto[]>> {
    return this.apiClient.get<CompanyDto[]>(this.base);
  }

  getById(companyId: number): Observable<ApiResponse<CompanyDto>> {
    return this.apiClient.get<CompanyDto>(`${this.base}/${companyId}`);
  }

  create(dto: CreateCompanyDto): Observable<ApiResponse<CompanyDto>> {
    return this.apiClient.post<CompanyDto>(this.base, dto);
  }

  update(companyId: number, dto: UpdateCompanyDto): Observable<ApiResponse<CompanyDto>> {
    return this.apiClient.put<CompanyDto>(`${this.base}/${companyId}`, dto);
  }

  updateLogo(companyId: number, file: File): Observable<ApiResponse<unknown>> {
    const form = new FormData();
    form.append('file', file);
    return this.apiClient.post<unknown>(`${this.base}/${companyId}/logo`, form);
  }
}
