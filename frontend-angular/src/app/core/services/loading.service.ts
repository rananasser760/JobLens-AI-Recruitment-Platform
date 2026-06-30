import { Injectable, computed, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class LoadingService {
  private readonly activeRequests = signal(0);

  readonly activeRequestsCount = computed(() => this.activeRequests());
  readonly isLoading = computed(() => this.activeRequests() > 0);

  startRequest(): void {
    this.activeRequests.update((count) => count + 1);
  }

  endRequest(): void {
    this.activeRequests.update((count) => Math.max(0, count - 1));
  }

  reset(): void {
    this.activeRequests.set(0);
  }
}