import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { ApiClientService } from './api-client.service';
import { ApiResponse } from '../models/api.model';
import { EnumMetadataDto } from '../models/metadata.model';

@Injectable({ providedIn: 'root' })
export class MetadataService {
  constructor(private readonly apiClient: ApiClientService) {}

  getEnums(): Observable<ApiResponse<EnumMetadataDto>> {
    return this.apiClient.get<EnumMetadataDto>('/metadata/enums');
  }
}
