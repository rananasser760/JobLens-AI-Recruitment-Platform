import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { CandidateListDto } from '../../../../core/models/candidate.model';
import { SessionFilterService } from '../../../../core/state/session-filter.service';
import { ErrorRetryComponent } from '../../../../shared/components/error-retry/error-retry.component';
import { CandidateService } from '../../../candidates/candidate.service';

const FILTER_SCOPE = 'recruiter.candidates';
const PAGE_SIZE_OPTIONS = [12, 24, 48] as const;

interface RecruiterCandidatesFilterState {
  keyword: string;
  location: string;
  skills: string;
  minExperience: string;
  maxExperience: string;
  pageNumber: number;
  pageSize: number;
}

@Component({
  selector: 'app-recruiter-candidates-page',
  imports: [CommonModule, RouterLink, ErrorRetryComponent],
  templateUrl: './recruiter-candidates.page.html',
  styleUrl: './recruiter-candidates.page.css'
})
export class RecruiterCandidatesPage {
  private readonly candidateService = inject(CandidateService);
  private readonly sessionFilters = inject(SessionFilterService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly candidates = signal<CandidateListDto[]>([]);
  readonly totalCount = signal(0);
  readonly totalPages = signal(1);
  readonly hasPreviousPage = signal(false);
  readonly hasNextPage = signal(false);

  readonly pageNumber = signal(1);
  readonly pageSize = signal<number>(PAGE_SIZE_OPTIONS[0]);
  readonly pageSizeOptions = signal<number[]>([...PAGE_SIZE_OPTIONS]);

  readonly keyword = signal('');
  readonly location = signal('');
  readonly skills = signal('');
  readonly minExperience = signal('');
  readonly maxExperience = signal('');

  constructor() {
    this.restoreFilters();
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.candidateService
      .search({
        pageNumber: this.pageNumber(),
        pageSize: this.pageSize(),
        keyword: this.keyword().trim() || undefined,
        location: this.location().trim() || undefined,
        skills: this.skills().trim() || undefined,
        minExperience: this.parseNumber(this.minExperience()),
        maxExperience: this.parseNumber(this.maxExperience())
      })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          const data = res.data;
          this.candidates.set(data?.items ?? []);
          this.totalCount.set(data?.totalCount ?? 0);
          this.totalPages.set(Math.max(1, data?.totalPages ?? 1));
          this.hasPreviousPage.set(data?.hasPreviousPage ?? false);
          this.hasNextPage.set(data?.hasNextPage ?? false);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load candidate search results right now.'));
        }
      });
  }

  onKeywordChanged(value: string): void {
    this.keyword.set(value);
    this.persistFilters();
  }

  onLocationChanged(value: string): void {
    this.location.set(value);
    this.persistFilters();
  }

  onSkillsChanged(value: string): void {
    this.skills.set(value);
    this.persistFilters();
  }

  onMinExperienceChanged(value: string): void {
    this.minExperience.set(value);
    this.persistFilters();
  }

  onMaxExperienceChanged(value: string): void {
    this.maxExperience.set(value);
    this.persistFilters();
  }

  applyFilters(): void {
    this.pageNumber.set(1);
    this.persistFilters();
    this.load();
  }

  clearFilters(): void {
    this.keyword.set('');
    this.location.set('');
    this.skills.set('');
    this.minExperience.set('');
    this.maxExperience.set('');
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

  private parseNumber(value: unknown): number | undefined {
    if (value === null || value === undefined) {
      return undefined;
    }

    if (typeof value === 'number') {
      return Number.isFinite(value) ? value : undefined;
    }

    if (typeof value !== 'string') {
      return undefined;
    }

    if (!value.trim()) {
      return undefined;
    }

    const parsed = Number(value);
    if (!Number.isFinite(parsed)) {
      return undefined;
    }

    return parsed;
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }

  private restoreFilters(): void {
    const saved = this.sessionFilters.read<RecruiterCandidatesFilterState>(FILTER_SCOPE, {
      keyword: '',
      location: '',
      skills: '',
      minExperience: '',
      maxExperience: '',
      pageNumber: 1,
      pageSize: PAGE_SIZE_OPTIONS[0]
    });

    this.keyword.set(saved.keyword ?? '');
    this.location.set(saved.location ?? '');
    this.skills.set(saved.skills ?? '');
    this.minExperience.set(saved.minExperience ?? '');
    this.maxExperience.set(saved.maxExperience ?? '');
    this.pageNumber.set(this.parsePositiveInt(saved.pageNumber, 1));
    this.pageSize.set(this.parsePageSize(saved.pageSize));
  }

  private persistFilters(): void {
    this.sessionFilters.write<RecruiterCandidatesFilterState>(FILTER_SCOPE, {
      keyword: this.keyword(),
      location: this.location(),
      skills: this.skills(),
      minExperience: this.minExperience(),
      maxExperience: this.maxExperience(),
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
