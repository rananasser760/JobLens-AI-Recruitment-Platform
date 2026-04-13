import { Injectable } from '@angular/core';
import { UserRole } from '../models/auth.model';
import { TokenStoreService } from './token-store.service';

@Injectable({ providedIn: 'root' })
export class AuthSessionService {
  constructor(private readonly tokens: TokenStoreService) {}

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

    const expiresAtMs = Date.parse(user.expiresAt);
    if (Number.isNaN(expiresAtMs)) {
      return true;
    }

    return Date.now() < expiresAtMs;
  }
}
