import { Inject, Injectable, PLATFORM_ID } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { environment } from '../../../environments/environment';
import { AuthResponseDto } from '../models/auth.model';

@Injectable({ providedIn: 'root' })
export class TokenStoreService {
  private readonly browser: boolean;

  constructor(@Inject(PLATFORM_ID) platformId: object) {
    this.browser = isPlatformBrowser(platformId);
  }

  getAccessToken(): string | null {
    if (!this.browser) {
      return null;
    }
    return localStorage.getItem(environment.authStorageKeys.accessToken);
  }

  getRefreshToken(): string | null {
    if (!this.browser) {
      return null;
    }
    return localStorage.getItem(environment.authStorageKeys.refreshToken);
  }

  getAuthUser(): AuthResponseDto | null {
    if (!this.browser) {
      return null;
    }

    const raw = localStorage.getItem(environment.authStorageKeys.authUser);
    if (!raw) {
      return null;
    }

    try {
      return JSON.parse(raw) as AuthResponseDto;
    } catch {
      this.clearSession();
      return null;
    }
  }

  setSession(payload: AuthResponseDto): void {
    if (!this.browser) {
      return;
    }

    localStorage.setItem(environment.authStorageKeys.accessToken, payload.accessToken);
    localStorage.setItem(environment.authStorageKeys.refreshToken, payload.refreshToken);
    localStorage.setItem(environment.authStorageKeys.authUser, JSON.stringify(payload));
  }

  clearSession(): void {
    if (!this.browser) {
      return;
    }

    localStorage.removeItem(environment.authStorageKeys.accessToken);
    localStorage.removeItem(environment.authStorageKeys.refreshToken);
    localStorage.removeItem(environment.authStorageKeys.authUser);
  }
}
