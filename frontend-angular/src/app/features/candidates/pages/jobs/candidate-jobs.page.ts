import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { ApplicationsService } from '../../../applications/applications.service';
import { JobsService } from '../../../jobs/jobs.service';
import { ResumeService } from '../../../resumes/resume.service';
import { EmploymentType, JobListDto, JobSource } from '../../../../core/models/job.model';
import { ResumeDto } from '../../../../core/models/resume.model';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';

const EMPLOYMENT_TYPES: EmploymentType[] = [
  'FullTime',
  'PartTime',
  'Contract',
  'Internship',
  'Freelance',
  'Remote'
];

const SOURCES: JobSource[] = ['Internal', 'Scraped'];
const PAGE_SIZE_OPTIONS = [12, 24, 48] as const;

@Component({
  selector: 'app-candidate-jobs-page',
  imports: [CommonModule, RouterLink, LoadingSpinnerComponent],
  templateUrl: './candidate-jobs.page.html',
  styleUrl: './candidate-jobs.page.css'
})
export class CandidateJobsPage {
  private readonly jobsService = inject(JobsService);
  private readonly applicationsService = inject(ApplicationsService);
  private readonly resumeService = inject(ResumeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly jobs = signal<JobListDto[]>([]);
  readonly totalCount = signal(0);
  readonly totalPages = signal(1);
  readonly hasPreviousPage = signal(false);
  readonly hasNextPage = signal(false);

  readonly pageNumber = signal(1);
  readonly pageSize = signal<number>(PAGE_SIZE_OPTIONS[0]);

  readonly appliedJobIds = signal<number[]>([]);
  readonly defaultResumeId = signal<number | null>(null);
  readonly applying = signal<Record<number, boolean>>({});

  readonly employmentTypes = signal<EmploymentType[]>([...EMPLOYMENT_TYPES]);
  readonly sources = signal<JobSource[]>([...SOURCES]);
  readonly pageSizeOptions = signal<number[]>([...PAGE_SIZE_OPTIONS]);

  readonly keyword = signal('');
  readonly location = signal('');
  readonly employmentType = signal<EmploymentType | ''>('');
  readonly source = signal<JobSource | ''>('');

  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      this.hydrateFromQuery(params);
      this.load();
    });
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      jobs: this.jobsService
        .search({
          pageNumber: this.pageNumber(),
          pageSize: this.pageSize(),
          sortBy: 'postedAt',
          sortDescending: true,
          isActive: true,
          keyword: this.keyword().trim() || undefined,
          location: this.location().trim() || undefined,
          employmentType: this.employmentType() || undefined,
          source: this.source() || undefined
        })
        .pipe(
          map((res) => {
            const data = res.data;

            return {
              items: data?.items ?? [],
              totalCount: data?.totalCount ?? 0,
              pageNumber: data?.pageNumber ?? this.pageNumber(),
              pageSize: data?.pageSize ?? this.pageSize(),
              totalPages: data?.totalPages ?? 1,
              hasPreviousPage: data?.hasPreviousPage ?? false,
              hasNextPage: data?.hasNextPage ?? false
            };
          }),
          catchError(() =>
            of({
              items: [] as JobListDto[],
              totalCount: 0,
              pageNumber: this.pageNumber(),
              pageSize: this.pageSize(),
              totalPages: 1,
              hasPreviousPage: false,
              hasNextPage: false
            })
          )
        ),
      appliedJobIds: this.applicationsService
        .getMyApplications({
          pageNumber: 1,
          pageSize: 200,
          sortBy: 'appliedAt',
          sortDescending: true
        })
        .pipe(
          map((res) => {
            const ids = (res.data?.items ?? []).map((item) => item.jobId);
            return [...new Set(ids)];
          }),
          catchError(() => of([] as number[]))
        ),
      resumes: this.resumeService
        .getMyResumes()
        .pipe(
          map((res) => res.data ?? []),
          catchError(() => of([] as ResumeDto[]))
        )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ jobs, appliedJobIds, resumes }) => {
          this.jobs.set(jobs.items);
          this.totalCount.set(jobs.totalCount);
          this.totalPages.set(Math.max(1, jobs.totalPages));
          this.hasPreviousPage.set(jobs.hasPreviousPage);
          this.hasNextPage.set(jobs.hasNextPage);

          if (jobs.pageNumber !== this.pageNumber()) {
            this.pageNumber.set(jobs.pageNumber);
          }
          if (jobs.pageSize !== this.pageSize()) {
            this.pageSize.set(jobs.pageSize);
          }

          this.appliedJobIds.set(appliedJobIds);

          const preferredResume = resumes.find((item) => item.isDefault) ?? resumes[0] ?? null;
          this.defaultResumeId.set(preferredResume?.id ?? null);
        },
        error: () => {
          this.error.set('Unable to load job listings right now.');
        }
      });
  }

  onKeywordChanged(value: string): void {
    this.keyword.set(value);
  }

  onLocationChanged(value: string): void {
    this.location.set(value);
  }

  onEmploymentTypeChanged(value: string): void {
    this.employmentType.set(this.toEmploymentType(value));
  }

  onSourceChanged(value: string): void {
    this.source.set(this.toSource(value));
  }

  applyFilters(): void {
    this.pageNumber.set(1);
    this.updateQueryParams();
  }

  clearFilters(): void {
    this.keyword.set('');
    this.location.set('');
    this.employmentType.set('');
    this.source.set('');
    this.pageNumber.set(1);
    this.updateQueryParams();
  }

  onPageSizeChanged(value: string): void {
    const parsed = Number(value);
    const nextPageSize = PAGE_SIZE_OPTIONS.includes(parsed as (typeof PAGE_SIZE_OPTIONS)[number])
      ? parsed
      : PAGE_SIZE_OPTIONS[0];

    this.pageSize.set(nextPageSize);
    this.pageNumber.set(1);
    this.updateQueryParams();
  }

  goToPreviousPage(): void {
    if (this.loading() || !this.hasPreviousPage()) {
      return;
    }

    this.pageNumber.set(Math.max(1, this.pageNumber() - 1));
    this.updateQueryParams();
  }

  goToNextPage(): void {
    if (this.loading() || !this.hasNextPage()) {
      return;
    }

    this.pageNumber.set(this.pageNumber() + 1);
    this.updateQueryParams();
  }

  applyToJob(jobId: number): void {
    if (this.isApplied(jobId) || this.isApplying(jobId)) {
      return;
    }

    this.setApplying(jobId, true);
    this.error.set(null);
    this.success.set(null);

    const resumeId = this.defaultResumeId();
    if (!resumeId) {
      this.setApplying(jobId, false);
      this.error.set('Please upload a resume before applying to jobs.');
      return;
    }

    this.applicationsService
      .apply({ jobId, resumeId })
      .pipe(finalize(() => this.setApplying(jobId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Application submitted successfully.');
          this.appliedJobIds.set([...new Set([...this.appliedJobIds(), jobId])]);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to apply to this job right now.'));
        }
      });
  }

  isApplying(jobId: number): boolean {
    return !!this.applying()[jobId];
  }

  isApplied(jobId: number): boolean {
    return this.appliedJobIds().includes(jobId);
  }

  isExternalJob(job: JobListDto): boolean {
    return job.source === 'Scraped';
  }

  shouldDisableApply(job: JobListDto): boolean {
    return this.isExternalJob(job) || this.isApplied(job.id) || this.isApplying(job.id);
  }

  getApplyLabel(job: JobListDto): string {
    if (this.isExternalJob(job)) {
      return 'External listing';
    }
    if (this.isApplied(job.id)) {
      return 'Applied';
    }
    if (this.isApplying(job.id)) {
      return 'Applying...';
    }
    return 'Apply now';
  }

  private setApplying(jobId: number, value: boolean): void {
    const next = { ...this.applying() };
    if (value) {
      next[jobId] = true;
    } else {
      delete next[jobId];
    }
    this.applying.set(next);
  }

  private toEmploymentType(value: string): EmploymentType | '' {
    if (EMPLOYMENT_TYPES.includes(value as EmploymentType)) {
      return value as EmploymentType;
    }
    return '';
  }

  private toSource(value: string): JobSource | '' {
    if (SOURCES.includes(value as JobSource)) {
      return value as JobSource;
    }
    return '';
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }

  private hydrateFromQuery(params: ParamMap): void {
    this.keyword.set(params.get('keyword') ?? '');
    this.location.set(params.get('location') ?? '');
    this.employmentType.set(this.toEmploymentType(params.get('employmentType') ?? ''));
    this.source.set(this.toSource(params.get('source') ?? ''));

    this.pageNumber.set(this.parsePositiveInt(params.get('page'), 1));
    this.pageSize.set(this.parsePageSize(params.get('pageSize')));
  }

  private updateQueryParams(): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        keyword: this.keyword().trim() || undefined,
        location: this.location().trim() || undefined,
        employmentType: this.employmentType() || undefined,
        source: this.source() || undefined,
        page: this.pageNumber() > 1 ? this.pageNumber() : undefined,
        pageSize: this.pageSize() !== PAGE_SIZE_OPTIONS[0] ? this.pageSize() : undefined
      },
      replaceUrl: true
    });
  }

  private parsePositiveInt(value: string | null, fallback: number): number {
    const parsed = Number(value);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback;
  }

  private parsePageSize(value: string | null): number {
    const parsed = Number(value);
    return PAGE_SIZE_OPTIONS.includes(parsed as (typeof PAGE_SIZE_OPTIONS)[number])
      ? parsed
      : PAGE_SIZE_OPTIONS[0];
  }
}
