import { Injectable, inject } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';
import { ApiClientService } from './api-client.service';
import { ApiResponse } from '../models/api.model';
import { EnumMetadataDto } from '../models/metadata.model';

@Injectable({ providedIn: 'root' })
export class MetadataService {
  private readonly apiClient = inject(ApiClientService);
  private enumCache$: Observable<ApiResponse<EnumMetadataDto>> | null = null;

  getEnums(forceRefresh = false): Observable<ApiResponse<EnumMetadataDto>> {
    if (!this.enumCache$ || forceRefresh) {
      this.enumCache$ = this.apiClient
        .get<EnumMetadataDto>('/metadata/enums')
        .pipe(shareReplay({ bufferSize: 1, refCount: false }));
    }
    return this.enumCache$;
  }
}
