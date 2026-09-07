import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { environment } from '../../../environments/environment';
import { AuthResponseDto } from '../models/auth.model';

@Injectable({ providedIn: 'root' })
export class TokenStoreService {
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

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
      const parsed = JSON.parse(raw);
      // Normalize expiresAtUtc / expiresAt property aliases
      if (parsed && !parsed.expiresAt && parsed.expiresAtUtc) {
        parsed.expiresAt = parsed.expiresAtUtc;
      }
      return parsed as AuthResponseDto;
    } catch {
      this.clearSession();
      return null;
    }
  }

  setSession(payload: AuthResponseDto): void {
    if (!this.browser) {
      return;
    }

    // Ensure cross-compatibility between expiresAt and expiresAtUtc
    const normalizedPayload = {
      ...payload,
      expiresAt:
        payload.expiresAt ?? (payload as unknown as { expiresAtUtc?: string }).expiresAtUtc,
      expiresAtUtc:
        (payload as unknown as { expiresAtUtc?: string }).expiresAtUtc ?? payload.expiresAt,
    };

    localStorage.setItem(environment.authStorageKeys.accessToken, payload.accessToken);
    localStorage.setItem(environment.authStorageKeys.refreshToken, payload.refreshToken);
    localStorage.setItem(environment.authStorageKeys.authUser, JSON.stringify(normalizedPayload));
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
