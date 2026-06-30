import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { MetadataService } from '../../../../core/api/metadata.service';
import { EnumOptionDto } from '../../../../core/models/metadata.model';
import { EmploymentType, JobListDto } from '../../../../core/models/job.model';
import { SessionFilterService } from '../../../../core/state/session-filter.service';
import { ErrorRetryComponent } from '../../../../shared/components/error-retry/error-retry.component';
import { JobsService } from '../../../jobs/jobs.service';

const FALLBACK_EMPLOYMENT_TYPES: EmploymentType[] = [
  'FullTime',
  'PartTime',
  'Contract',
  'Internship',
  'Freelance',
  'Remote'
];

const FILTER_SCOPE = 'recruiter.jobs';
const PAGE_SIZE_OPTIONS = [12, 24, 48] as const;

interface RecruiterJobsFilterState {
  keyword: string;
  location: string;
  employmentType: EmploymentType | '';
  pageNumber: number;
  pageSize: number;
}

@Component({
  selector: 'app-recruiter-jobs-page',
  imports: [CommonModule, RouterLink, ErrorRetryComponent],
  templateUrl: './recruiter-jobs.page.html',
  styleUrl: './recruiter-jobs.page.css'
})
export class RecruiterJobsPage {
  private readonly jobsService = inject(JobsService);
  private readonly metadataService = inject(MetadataService);
  private readonly sessionFilters = inject(SessionFilterService);
  private readonly router = inject(Router);

  readonly loading = signal(true);
  readonly toggling = signal<Record<number, boolean>>({});
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly jobs = signal<JobListDto[]>([]);
  readonly totalCount = signal(0);
  readonly totalPages = signal(1);
  readonly hasPreviousPage = signal(false);
  readonly hasNextPage = signal(false);

  readonly pageNumber = signal(1);
  readonly pageSize = signal<number>(PAGE_SIZE_OPTIONS[0]);
  readonly pageSizeOptions = signal<number[]>([...PAGE_SIZE_OPTIONS]);

  readonly employmentTypes = signal<EmploymentType[]>([...FALLBACK_EMPLOYMENT_TYPES]);

  readonly keyword = signal('');
  readonly location = signal('');
  readonly employmentType = signal<EmploymentType | ''>('');

  constructor() {
    this.restoreFilters();
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      jobs: this.jobsService
        .getMyJobs({
          pageNumber: this.pageNumber(),
          pageSize: this.pageSize(),
          sortBy: 'postedAt',
          sortDescending: true,
          keyword: this.keyword().trim() || undefined,
          location: this.location().trim() || undefined,
          employmentType: this.employmentType() || undefined
        })
        .pipe(
          map((res) => {
            const data = res.data;
            return {
              items: data?.items ?? [],
              totalCount: data?.totalCount ?? 0,
              totalPages: data?.totalPages ?? 1,
              hasPreviousPage: data?.hasPreviousPage ?? false,
              hasNextPage: data?.hasNextPage ?? false
            };
          }),
          catchError(() =>
            of({
              items: [] as JobListDto[],
              totalCount: 0,
              totalPages: 1,
              hasPreviousPage: false,
              hasNextPage: false
            })
          )
        ),
      employmentTypes: this.metadataService.getEnums().pipe(
        map((res) => this.extractEmploymentTypes(res.data?.enums?.['EmploymentType'] ?? [])),
        catchError(() => of([...FALLBACK_EMPLOYMENT_TYPES]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ jobs, employmentTypes }) => {
          this.jobs.set(jobs.items);
          this.totalCount.set(jobs.totalCount);
          this.totalPages.set(Math.max(1, jobs.totalPages));
          this.hasPreviousPage.set(jobs.hasPreviousPage);
          this.hasNextPage.set(jobs.hasNextPage);
          this.employmentTypes.set(employmentTypes);
        },
        error: () => {
          this.error.set('Unable to load recruiter jobs right now.');
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

  onEmploymentTypeChanged(value: string): void {
    this.employmentType.set(this.toEmploymentType(value));
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
    this.employmentType.set('');
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

  toggleStatus(jobId: number): void {
    if (this.isToggling(jobId)) {
      return;
    }

    this.setToggling(jobId, true);
    this.error.set(null);
    this.success.set(null);

    this.jobsService
      .toggleStatus(jobId)
      .pipe(finalize(() => this.setToggling(jobId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || `Job ${jobId} status updated.`);
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to toggle this job status right now.'));
        }
      });
  }

  openDetails(jobId: number): void {
    void this.router.navigate(['/recruiter/jobs', jobId]);
  }

  isToggling(jobId: number): boolean {
    return !!this.toggling()[jobId];
  }

  private setToggling(jobId: number, value: boolean): void {
    const next = { ...this.toggling() };
    if (value) {
      next[jobId] = true;
    } else {
      delete next[jobId];
    }
    this.toggling.set(next);
  }

  private extractEmploymentTypes(options: EnumOptionDto[]): EmploymentType[] {
    const parsed = options
      .map((item) => item.name)
      .filter((name): name is EmploymentType => FALLBACK_EMPLOYMENT_TYPES.includes(name as EmploymentType));

    if (parsed.length === 0) {
      return [...FALLBACK_EMPLOYMENT_TYPES];
    }

    return parsed;
  }

  private toEmploymentType(value: string): EmploymentType | '' {
    if (FALLBACK_EMPLOYMENT_TYPES.includes(value as EmploymentType)) {
      return value as EmploymentType;
    }
    return '';
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }

  private restoreFilters(): void {
    const saved = this.sessionFilters.read<RecruiterJobsFilterState>(FILTER_SCOPE, {
      keyword: '',
      location: '',
      employmentType: '',
      pageNumber: 1,
      pageSize: PAGE_SIZE_OPTIONS[0]
    });

    this.keyword.set(saved.keyword ?? '');
    this.location.set(saved.location ?? '');
    this.employmentType.set(this.toEmploymentType(saved.employmentType ?? ''));
    this.pageNumber.set(this.parsePositiveInt(saved.pageNumber, 1));
    this.pageSize.set(this.parsePageSize(saved.pageSize));
  }

  private persistFilters(): void {
    this.sessionFilters.write<RecruiterJobsFilterState>(FILTER_SCOPE, {
      keyword: this.keyword(),
      location: this.location(),
      employmentType: this.employmentType(),
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
