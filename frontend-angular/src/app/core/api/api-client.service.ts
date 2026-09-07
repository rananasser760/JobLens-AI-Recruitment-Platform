import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ApiResponse } from '../models/api.model';

@Injectable({ providedIn: 'root' })
export class ApiClientService {
  private readonly http = inject(HttpClient);

  // Normalize base and prefix (e.g., "http://localhost:5245" and "/api")
  private readonly baseUrl = (environment.apiBaseUrl || '').replace(/\/+$/, '');
  private readonly apiPrefix = (environment.apiPrefix || '/api').replace(/^\/+|\/+$/g, '');

  buildUrl(path: string): string {
    if (path.startsWith('http://') || path.startsWith('https://')) {
      return path;
    }

    let cleanPath = path.replace(/^\/+/, '');

    // Prevent duplicate "/api/api/..." if caller includes "/api" in their path
    if (cleanPath.startsWith(this.apiPrefix + '/')) {
      cleanPath = cleanPath.slice(this.apiPrefix.length + 1);
    } else if (cleanPath === this.apiPrefix) {
      cleanPath = '';
    }

    return cleanPath.length > 0
      ? `${this.baseUrl}/${this.apiPrefix}/${cleanPath}`
      : `${this.baseUrl}/${this.apiPrefix}`;
  }

  toHttpParams(params?: Record<string, unknown>): HttpParams {
    if (!params) {
      return new HttpParams();
    }

    let httpParams = new HttpParams();

    for (const [key, value] of Object.entries(params)) {
      if (value === undefined || value === null || value === '') {
        continue;
      }

      if (Array.isArray(value)) {
        for (const item of value) {
          if (item !== undefined && item !== null && item !== '') {
            httpParams = httpParams.append(key, String(item));
          }
        }
      } else if (typeof value === 'boolean') {
        httpParams = httpParams.set(key, value ? 'true' : 'false');
      } else {
        httpParams = httpParams.set(key, String(value));
      }
    }

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

  patch<T>(
    path: string,
    body: unknown,
    params?: Record<string, unknown>,
  ): Observable<ApiResponse<T>> {
    return this.http.patch<ApiResponse<T>>(this.buildUrl(path), body, {
      params: this.toHttpParams(params),
    });
  }

  delete<T>(path: string, params?: Record<string, unknown>): Observable<ApiResponse<T>> {
    return this.http.delete<ApiResponse<T>>(this.buildUrl(path), {
      params: this.toHttpParams(params),
    });
  }

  downloadBlob(path: string, params?: Record<string, unknown>): Observable<Blob> {
    return this.http.get(this.buildUrl(path), {
      params: this.toHttpParams(params),
      responseType: 'blob',
    });
  }
}
