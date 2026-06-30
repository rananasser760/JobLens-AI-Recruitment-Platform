import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class SessionFilterService {
  private readonly prefix = 'joblens.filters';

  read<T>(scope: string, fallback: T): T {
    if (typeof window === 'undefined') {
      return fallback;
    }

    try {
      const raw = window.sessionStorage.getItem(this.buildKey(scope));
      if (!raw) {
        return fallback;
      }

      return JSON.parse(raw) as T;
    } catch {
      return fallback;
    }
  }

  write<T>(scope: string, value: T): void {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.sessionStorage.setItem(this.buildKey(scope), JSON.stringify(value));
    } catch {
      // Ignore storage quota or serialization issues.
    }
  }

  clear(scope: string): void {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.sessionStorage.removeItem(this.buildKey(scope));
    } catch {
      // Ignore storage access issues.
    }
  }

  private buildKey(scope: string): string {
    return `${this.prefix}.${scope}`;
  }
}