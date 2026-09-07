import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { catchError, map, tap } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { ApiResponse } from '../models/api.model';
import {
  AuthResponseDto,
  ChangePasswordDto,
  ForgotPasswordDto,
  LoginDto,
  RefreshTokenDto,
  RegisterDto,
  ResetPasswordDto,
} from '../models/auth.model';
import { TokenStoreService } from './token-store.service';
import { ChatService } from '../services/chat.service';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly tokens = inject(TokenStoreService);
  private readonly chatService = inject(ChatService);

  readonly currentUser = signal<AuthResponseDto | null>(null);

  private readonly baseUrl = (environment.apiBaseUrl || '').replace(/\/+$/, '');
  private readonly apiPrefix = (environment.apiPrefix || '/api').replace(/^\/+|\/+$/g, '');
  private readonly base = `${this.baseUrl}/${this.apiPrefix}/auth`;

  constructor() {
    this.currentUser.set(this.tokens.getAuthUser());
  }

  login(dto: LoginDto): Observable<AuthResponseDto> {
    return this.http.post<ApiResponse<AuthResponseDto>>(`${this.base}/login`, dto).pipe(
      map((res) => this.unwrap(res, 'Login failed')),
      tap((session) => this.setSession(session)),
    );
  }

  register(dto: RegisterDto): Observable<AuthResponseDto> {
    return this.http.post<ApiResponse<AuthResponseDto>>(`${this.base}/register`, dto).pipe(
      map((res) => this.unwrap(res, 'Registration failed')),
      tap((session) => this.setSession(session)),
    );
  }

  refreshToken(): Observable<AuthResponseDto> {
    const accessToken = this.tokens.getAccessToken();
    const refreshToken = this.tokens.getRefreshToken();

    if (!accessToken || !refreshToken) {
      this.clearSession();
      return throwError(() => new Error('No refresh credentials found.'));
    }

    const payload: RefreshTokenDto = { accessToken, refreshToken };

    return this.http.post<ApiResponse<AuthResponseDto>>(`${this.base}/refresh-token`, payload).pipe(
      map((res) => this.unwrap(res, 'Refresh token failed')),
      tap((session) => this.setSession(session)),
      catchError((err) => {
        this.clearSession();
        return throwError(() => err);
      }),
    );
  }

  logout(): Observable<void> {
    const accessToken = this.tokens.getAccessToken();
    if (accessToken) {
      this.http
        .post(
          `${this.base}/logout`,
          {},
          {
            headers: { Authorization: `Bearer ${accessToken}` },
          },
        )
        .pipe(catchError(() => of(null)))
        .subscribe();
    }

    this.clearSession();
    return of(void 0);
  }

  hardLogout(): void {
    this.clearSession();
  }

  validateToken(accessToken: string): Observable<boolean> {
    return this.http
      .get<ApiResponse<unknown>>(`${this.base}/validate`, {
        headers: {
          Authorization: `Bearer ${accessToken}`,
        },
      })
      .pipe(
        map((res) => !!res.success),
        catchError(() => of(false)),
      );
  }

  changePassword(dto: ChangePasswordDto): Observable<ApiResponse<unknown>> {
    return this.http.post<ApiResponse<unknown>>(`${this.base}/change-password`, dto);
  }

  forgotPassword(dto: ForgotPasswordDto): Observable<ApiResponse<unknown>> {
    return this.http.post<ApiResponse<unknown>>(`${this.base}/forgot-password`, dto);
  }

  resetPassword(dto: ResetPasswordDto): Observable<ApiResponse<unknown>> {
    return this.http.post<ApiResponse<unknown>>(`${this.base}/reset-password`, dto);
  }

  private setSession(session: AuthResponseDto): void {
    this.tokens.setSession(session);
    this.currentUser.set(session);
    this.chatService.startConnection().catch((err) => {
      console.warn('Realtime chat connection could not be established immediately:', err);
    });
  }

  private clearSession(): void {
    this.tokens.clearSession();
    this.currentUser.set(null);
    this.chatService.stopConnection().catch(() => {
      // Ignored during session tear-down
    });
  }

  private unwrap<T>(response: ApiResponse<T>, fallbackMessage: string): T {
    if (!response.success || !response.data) {
      throw new Error(response.message || fallbackMessage);
    }
    return response.data;
  }
}
