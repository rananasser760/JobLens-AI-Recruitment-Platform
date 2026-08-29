import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformServer } from '@angular/common';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ApiResponse } from '../models/api.model';

@Injectable({ providedIn: 'root' })
export class ApiClientService {
  private readonly platformId = inject(PLATFORM_ID);

  private get apiRoot(): string {
    // If executing via SSR inside Docker, route internally via gateway container name
    if (isPlatformServer(this.platformId)) {
      const internalUrl = process.env['INTERNAL_API_URL'] || 'http://gateway:8080';
      return `${internalUrl}${environment.apiPrefix}`;
    }
    // Standard browser execution
    return `${environment.apiBaseUrl}${environment.apiPrefix}`;
  }

  constructor(private readonly http: HttpClient) {}

  buildUrl(path: string): string {
    const normalized = path.startsWith('/') ? path : `/${path}`;
    return `${this.apiRoot}${normalized}`;
  }

  toHttpParams(params?: Record<string, unknown>): HttpParams {
    if (!params) {
      return new HttpParams();
    }

    let httpParams = new HttpParams();
    Object.entries(params).forEach(([key, value]) => {
      if (value === undefined || value === null || value === '') {
        return;
      }
      httpParams = httpParams.set(key, String(value));
    });
    return httpParams;
  }

  get<T>(path: string, params?: Record<string, unknown>): Observable<ApiResponse<T>> {
    return this.http.get<ApiResponse<T>>(this.buildUrl(path), {
      params: this.toHttpParams(params),
    });
  }

  post<T>(
    path: string,
    body: unknown,
    params?: Record<string, unknown>,
  ): Observable<ApiResponse<T>> {
    return this.http.post<ApiResponse<T>>(this.buildUrl(path), body, {
      params: this.toHttpParams(params),
    });
  }

  put<T>(
    path: string,
    body: unknown,
    params?: Record<string, unknown>,
  ): Observable<ApiResponse<T>> {
    return this.http.put<ApiResponse<T>>(this.buildUrl(path), body, {
      params: this.toHttpParams(params),
    });
  }

  delete<T>(path: string, params?: Record<string, unknown>): Observable<ApiResponse<T>> {
    return this.http.delete<ApiResponse<T>>(this.buildUrl(path), {
      params: this.toHttpParams(params),
    });
  }
}
