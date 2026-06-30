import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { map, Observable, of, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ApiResponse } from '../models/api.model';
import {
  AuthResponseDto,
  ChangePasswordDto,
  ForgotPasswordDto,
  LoginDto,
  RefreshTokenDto,
  RegisterDto,
  ResetPasswordDto
} from '../models/auth.model';
import { TokenStoreService } from './token-store.service';
import { ChatService } from '../services/chat.service';

@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly currentUser = signal<AuthResponseDto | null>(null);

  private readonly base = `${environment.apiBaseUrl}${environment.apiPrefix}/auth`;

  constructor(
    private readonly http: HttpClient,
    private readonly tokens: TokenStoreService,
    private readonly chatService: ChatService
  ) {
    this.currentUser.set(this.tokens.getAuthUser());
  }

  login(dto: LoginDto): Observable<AuthResponseDto> {
    return this.http.post<ApiResponse<AuthResponseDto>>(`${this.base}/login`, dto).pipe(
      map((res) => this.unwrap(res, 'Login failed')),
      tap((session) => this.setSession(session))
    );
  }

  register(dto: RegisterDto): Observable<AuthResponseDto> {
    return this.http.post<ApiResponse<AuthResponseDto>>(`${this.base}/register`, dto).pipe(
      map((res) => this.unwrap(res, 'Registration failed')),
      tap((session) => this.setSession(session))
    );
  }

  refreshToken(): Observable<AuthResponseDto> {
    const payload: RefreshTokenDto = {
      accessToken: this.tokens.getAccessToken() ?? '',
      refreshToken: this.tokens.getRefreshToken() ?? ''
    };

    return this.http.post<ApiResponse<AuthResponseDto>>(`${this.base}/refresh-token`, payload).pipe(
      map((res) => this.unwrap(res, 'Refresh token failed')),
      tap((session) => this.setSession(session))
    );
  }

  logout(): Observable<void> {
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
          Authorization: `Bearer ${accessToken}`
        }
      })
      .pipe(map((res) => !!res.success));
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
    this.chatService.startConnection();
  }

  private clearSession(): void {
    this.tokens.clearSession();
    this.currentUser.set(null);
    this.chatService.stopConnection();
  }

  private unwrap<T>(response: ApiResponse<T>, fallbackMessage: string): T {
    if (!response.success || !response.data) {
      throw new Error(response.message || fallbackMessage);
    }
    return response.data;
  }
}
