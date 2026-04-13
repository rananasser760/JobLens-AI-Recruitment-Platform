import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { InterviewSessionListDto } from '../../../../core/models/interview.model';
import { SessionFilterService } from '../../../../core/state/session-filter.service';
import { ErrorRetryComponent } from '../../../../shared/components/error-retry/error-retry.component';
import { InterviewsService } from '../../../interviews/interviews.service';

const FILTER_SCOPE = 'recruiter.interviews';
const PAGE_SIZE_OPTIONS = [12, 24, 48] as const;

interface RecruiterInterviewsFilterState {
  statusFilter: string;
  pageNumber: number;
  pageSize: number;
}

@Component({
  selector: 'app-recruiter-interviews-page',
  imports: [CommonModule, RouterLink, ErrorRetryComponent],
  templateUrl: './recruiter-interviews.page.html',
  styleUrl: './recruiter-interviews.page.css'
})
export class RecruiterInterviewsPage {
  private readonly interviewsService = inject(InterviewsService);
  private readonly sessionFilters = inject(SessionFilterService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly interviews = signal<InterviewSessionListDto[]>([]);
  readonly totalCount = signal(0);
  readonly totalPages = signal(1);
  readonly hasPreviousPage = signal(false);
  readonly hasNextPage = signal(false);

  readonly pageNumber = signal(1);
  readonly pageSize = signal<number>(PAGE_SIZE_OPTIONS[0]);
  readonly pageSizeOptions = signal<number[]>([...PAGE_SIZE_OPTIONS]);

  readonly statusFilter = signal('');

  constructor() {
    this.restoreFilters();
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.interviewsService
      .getRecruiterInterviews({
        pageNumber: this.pageNumber(),
        pageSize: this.pageSize(),
        sortBy: 'scheduledAt',
        status: this.statusFilter().trim() || undefined
      })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          const data = res.data;
          this.interviews.set(data?.items ?? []);
          this.totalCount.set(data?.totalCount ?? 0);
          this.totalPages.set(Math.max(1, data?.totalPages ?? 1));
          this.hasPreviousPage.set(data?.hasPreviousPage ?? false);
          this.hasNextPage.set(data?.hasNextPage ?? false);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load interviews right now.'));
        }
      });
  }

  onStatusFilterChanged(value: string): void {
    this.statusFilter.set(value);
    this.persistFilters();
  }

  applyFilter(): void {
    this.pageNumber.set(1);
    this.persistFilters();
    this.load();
  }

  clearFilter(): void {
    this.statusFilter.set('');
    this.pageNumber.set(1);
    this.persistFilters();
    this.load();
  }

  onPageSizeChanged(value: string): void {
    const parsed = Number(value);
    const nextPageSize = PAGE_SIZE_OPTIONS.includes(parsed as (typeof PAGE_SIZE_OPTIONS)[number])
      ? parsed
      : PAGE_SIZE_OPTIONS[0];

    this.pageSize.set(nextPageSize);
    this.pageNumber.set(1);
    this.persistFilters();
    this.load();
  }

  goToPreviousPage(): void {
    if (this.loading() || !this.hasPreviousPage()) {
      return;
    }

    this.pageNumber.set(Math.max(1, this.pageNumber() - 1));
    this.persistFilters();
    this.load();
  }

  goToNextPage(): void {
    if (this.loading() || !this.hasNextPage()) {
      return;
    }

    this.pageNumber.set(this.pageNumber() + 1);
    this.persistFilters();
    this.load();
  }

  canViewReport(status: string, score?: number | null): boolean {
    if (score !== null && score !== undefined) {
      return true;
    }

    const normalized = status.trim().toLowerCase();
    return normalized.includes('completed');
  }

  formatStatus(status: string): string {
    return status
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/_/g, ' ')
      .trim();
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }

  private restoreFilters(): void {
    const saved = this.sessionFilters.read<RecruiterInterviewsFilterState>(FILTER_SCOPE, {
      statusFilter: '',
      pageNumber: 1,
      pageSize: PAGE_SIZE_OPTIONS[0]
    });

    this.statusFilter.set(saved.statusFilter ?? '');
    this.pageNumber.set(this.parsePositiveInt(saved.pageNumber, 1));
    this.pageSize.set(this.parsePageSize(saved.pageSize));
  }

  private persistFilters(): void {
    this.sessionFilters.write<RecruiterInterviewsFilterState>(FILTER_SCOPE, {
      statusFilter: this.statusFilter(),
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize()
    });
  }

  private parsePositiveInt(value: unknown, fallback: number): number {
    if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
      return fallback;
    }

    return Math.floor(value);
  }

  private parsePageSize(value: unknown): number {
    if (typeof value !== 'number') {
      return PAGE_SIZE_OPTIONS[0];
    }

    return PAGE_SIZE_OPTIONS.includes(value as (typeof PAGE_SIZE_OPTIONS)[number])
      ? value
      : PAGE_SIZE_OPTIONS[0];
  }
}
