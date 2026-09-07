import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError, map, tap } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { ApiResponse } from '../models/api.model';
import { AuthResponseDto, RefreshTokenDto, UserRole } from '../models/auth.model';
import { TokenStoreService } from './token-store.service';

@Injectable({ providedIn: 'root' })
export class AuthSessionService {
  private readonly http = inject(HttpClient);
  private readonly tokens = inject(TokenStoreService);

  private readonly baseUrl = (environment.apiBaseUrl || '').replace(/\/+$/, '');
  private readonly apiPrefix = (environment.apiPrefix || '/api').replace(/^\/+|\/+$/g, '');
  private readonly refreshUrl = `${this.baseUrl}/${this.apiPrefix}/auth/refresh-token`;

  getCurrentRole(): UserRole | null {
    return this.tokens.getAuthUser()?.role ?? null;
  }

  hasAnyRole(roles: UserRole[]): boolean {
    const current = this.getCurrentRole();
    return !!current && roles.includes(current);
  }

  isAuthenticated(): boolean {
    const user = this.tokens.getAuthUser();
    const token = this.tokens.getAccessToken();
    if (!user || !token) {
      return false;
    }

    // Support both expiresAtUtc (backend standard) and expiresAt (compat alias)
    const rawExpiry = (user as unknown as { expiresAtUtc?: string }).expiresAtUtc ?? user.expiresAt;
    if (!rawExpiry) {
      return true;
    }

    const expiresAtMs = Date.parse(rawExpiry);
    if (Number.isNaN(expiresAtMs)) {
      return true;
    }

    // Buffer token validity by 10 seconds to handle clock drift
    return Date.now() < expiresAtMs - 10000;
  }

  /**
   * Refreshes access token for the HTTP interceptor without creating a circular dependency with AuthService.
   */
  refreshToken(): Observable<string> {
    const accessToken = this.tokens.getAccessToken();
    const refreshToken = this.tokens.getRefreshToken();

    if (!accessToken || !refreshToken) {
      this.tokens.clearSession();
      return throwError(() => new Error('Missing token pair for refresh.'));
    }

    const payload: RefreshTokenDto = { accessToken, refreshToken };

    return this.http.post<ApiResponse<AuthResponseDto>>(this.refreshUrl, payload).pipe(
      map((res) => {
        if (!res.success || !res.data) {
          throw new Error(res.message || 'Token refresh failed.');
        }
        return res.data;
      }),
      tap((session) => {
        this.tokens.setSession(session);
      }),
      map((session) => session.accessToken),
      catchError((err) => {
        this.tokens.clearSession();
        return throwError(() => err);
      }),
    );
  }
}
