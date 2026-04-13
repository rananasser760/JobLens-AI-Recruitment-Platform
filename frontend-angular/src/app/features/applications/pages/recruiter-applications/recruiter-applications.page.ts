import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { MetadataService } from '../../../../core/api/metadata.service';
import {
  ApplicationStatus,
  BulkUpdateApplicationStatusResultDto,
  CandidateApplicationDto
} from '../../../../core/models/application.model';
import { InterviewAgentType } from '../../../../core/models/interview.model';
import { EnumOptionDto } from '../../../../core/models/metadata.model';
import { JobListDto } from '../../../../core/models/job.model';
import { SessionFilterService } from '../../../../core/state/session-filter.service';
import { ErrorRetryComponent } from '../../../../shared/components/error-retry/error-retry.component';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { ApplicationsService } from '../../applications.service';
import { InterviewsService } from '../../../interviews/interviews.service';
import { JobsService } from '../../../jobs/jobs.service';

const FALLBACK_STATUSES: ApplicationStatus[] = [
  'Submitted',
  'AtsPending',
  'AtsQualified',
  'AtsRejected',
  'InterviewScheduled',
  'InterviewCompleted',
  'Offered',
  'Rejected',
  'Withdrawn',
  'ExternalRedirected'
];

const INTERVIEW_AGENT_TYPES: InterviewAgentType[] = ['Technical', 'Behavioral', 'Mixed'];
const FILTER_SCOPE = 'recruiter.applications';
const PAGE_SIZE_OPTIONS = [12, 24, 48] as const;

interface RecruiterApplicationsFilterState {
  selectedJobId: number | null;
  filterStatus: ApplicationStatus | '';
  bulkStatus: ApplicationStatus | '';
  bulkNotes: string;
  pageNumber: number;
  pageSize: number;
}

@Component({
  selector: 'app-recruiter-applications-page',
  imports: [CommonModule, ErrorRetryComponent, LoadingSpinnerComponent],
  templateUrl: './recruiter-applications.page.html',
  styleUrl: './recruiter-applications.page.css'
})
export class RecruiterApplicationsPage {
  private readonly router = inject(Router);
  private readonly jobsService = inject(JobsService);
  private readonly metadataService = inject(MetadataService);
  private readonly applicationsService = inject(ApplicationsService);
  private readonly interviewsService = inject(InterviewsService);
  private readonly sessionFilters = inject(SessionFilterService);

  readonly loading = signal(true);
  readonly submitting = signal(false);
  readonly scheduling = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);
  readonly showConfirmModal = signal(false);
  readonly showScheduleModal = signal(false);
  readonly bulkResult = signal<BulkUpdateApplicationStatusResultDto | null>(null);

  readonly jobs = signal<JobListDto[]>([]);
  readonly selectedJobId = signal<number | null>(null);

  readonly totalCount = signal(0);
  readonly totalPages = signal(1);
  readonly hasPreviousPage = signal(false);
  readonly hasNextPage = signal(false);

  readonly pageNumber = signal(1);
  readonly pageSize = signal<number>(PAGE_SIZE_OPTIONS[0]);
  readonly pageSizeOptions = signal<number[]>([...PAGE_SIZE_OPTIONS]);

  readonly statusOptions = signal<ApplicationStatus[]>([...FALLBACK_STATUSES]);
  readonly filterStatus = signal<ApplicationStatus | ''>('');
  readonly bulkStatus = signal<ApplicationStatus | ''>('');
  readonly bulkNotes = signal('');

  readonly scheduleAgentTypes = signal<InterviewAgentType[]>([...INTERVIEW_AGENT_TYPES]);
  readonly scheduleApplicationId = signal<number | null>(null);
  readonly scheduleCandidateName = signal('');
  readonly scheduleJobTitle = signal('');
  readonly scheduleTitle = signal('');
  readonly scheduleDateTime = signal('');
  readonly scheduleAgentType = signal<InterviewAgentType>('Mixed');

  readonly applications = signal<CandidateApplicationDto[]>([]);
  readonly selectedApplicationIds = signal<number[]>([]);
  readonly rowStatuses = signal<Record<number, ApplicationStatus | ''>>({});
  readonly rowNotes = signal<Record<number, string>>({});
  readonly rowSubmitting = signal<Record<number, boolean>>({});

  readonly selectedCount = computed(() => this.selectedApplicationIds().length);
  readonly hasSelection = computed(() => this.selectedCount() > 0);
  readonly bulkResultHasIssues = computed(() => {
    const result = this.bulkResult();
    if (!result) {
      return false;
    }

    return result.skippedCount > 0 || result.notFoundIds.length > 0 || result.unauthorizedIds.length > 0;
  });
  readonly allSelected = computed(() => {
    const items = this.applications();
    if (items.length === 0) {
      return false;
    }

    const selected = new Set(this.selectedApplicationIds());
    return items.every((item) => selected.has(item.applicationId));
  });

  constructor() {
    this.restoreFilterState();
    this.loadContext();
  }

  loadContext(): void {
    this.loading.set(true);
    this.error.set(null);
    this.success.set(null);
    this.bulkResult.set(null);
    this.showConfirmModal.set(false);

    forkJoin({
      jobs: this.jobsService
        .getMyJobs({ pageNumber: 1, pageSize: 100, sortBy: 'postedAt', sortDescending: true })
        .pipe(
          map((res) => res.data?.items ?? []),
          catchError(() => of([] as JobListDto[]))
        ),
      statusOptions: this.metadataService.getEnums().pipe(
        map((res) => this.extractStatusOptions(res.data?.enums?.['ApplicationStatus'] ?? [])),
        catchError(() => of([...FALLBACK_STATUSES]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ jobs, statusOptions }) => {
          this.jobs.set(jobs);
          this.statusOptions.set(statusOptions);

          if (jobs.length === 0) {
            this.selectedJobId.set(null);
            this.applications.set([]);
            this.totalCount.set(0);
            this.totalPages.set(1);
            this.hasPreviousPage.set(false);
            this.hasNextPage.set(false);
            this.selectedApplicationIds.set([]);
            this.rowStatuses.set({});
            this.rowNotes.set({});
            this.rowSubmitting.set({});
            this.persistFilterState();
            return;
          }

          const currentSelected = this.selectedJobId();
          const hasCurrentSelection =
            currentSelected !== null && jobs.some((job) => job.id === currentSelected);
          this.selectedJobId.set(hasCurrentSelection ? currentSelected : jobs[0].id);
          this.persistFilterState();
          this.loadApplications();
        },
        error: () => {
          this.error.set('Unable to load application management context right now.');
        }
      });
  }

  loadApplications(): void {
    const jobId = this.selectedJobId();
    if (!jobId) {
      this.applications.set([]);
      this.selectedApplicationIds.set([]);
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.applicationsService
      .getJobApplications(jobId, {
        pageNumber: this.pageNumber(),
        pageSize: this.pageSize(),
        sortBy: 'appliedAt',
        sortDescending: true,
        status: this.filterStatus() || undefined
      })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          const data = res.data;
          const items = data?.items ?? [];
          this.applications.set(items);
          this.totalCount.set(data?.totalCount ?? 0);
          this.totalPages.set(Math.max(1, data?.totalPages ?? 1));
          this.hasPreviousPage.set(data?.hasPreviousPage ?? false);
          this.hasNextPage.set(data?.hasNextPage ?? false);
          this.syncRowDrafts(items);

          const validIds = new Set(items.map((item) => item.applicationId));
          this.selectedApplicationIds.set(
            this.selectedApplicationIds().filter((id) => validIds.has(id))
          );
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load applications for this job.'));
        }
      });
  }

  onJobChanged(value: string): void {
    const nextId = Number(value);
    this.selectedJobId.set(Number.isFinite(nextId) ? nextId : null);
    this.pageNumber.set(1);
    this.selectedApplicationIds.set([]);
    this.bulkResult.set(null);
    this.persistFilterState();
    this.loadApplications();
  }

  onFilterStatusChanged(value: string): void {
    this.filterStatus.set(this.toApplicationStatus(value));
    this.pageNumber.set(1);
    this.selectedApplicationIds.set([]);
    this.bulkResult.set(null);
    this.persistFilterState();
    this.loadApplications();
  }

  onBulkStatusChanged(value: string): void {
    this.bulkStatus.set(this.toApplicationStatus(value));
    this.persistFilterState();
  }

  onBulkNotesChanged(value: string): void {
    this.bulkNotes.set(value);
    this.persistFilterState();
  }

  onPageSizeChanged(value: string): void {
    const parsed = Number(value);
    const nextPageSize = PAGE_SIZE_OPTIONS.includes(parsed as (typeof PAGE_SIZE_OPTIONS)[number])
      ? parsed
      : PAGE_SIZE_OPTIONS[0];

    this.pageSize.set(nextPageSize);
    this.pageNumber.set(1);
    this.persistFilterState();
    this.loadApplications();
  }

  goToPreviousPage(): void {
    if (this.loading() || !this.hasPreviousPage()) {
      return;
    }

    this.pageNumber.set(Math.max(1, this.pageNumber() - 1));
    this.persistFilterState();
    this.loadApplications();
  }

  goToNextPage(): void {
    if (this.loading() || !this.hasNextPage()) {
      return;
    }

    this.pageNumber.set(this.pageNumber() + 1);
    this.persistFilterState();
    this.loadApplications();
  }

  getRowStatus(applicationId: number): ApplicationStatus | '' {
    return this.rowStatuses()[applicationId] ?? '';
  }

  onRowStatusChanged(applicationId: number, value: string): void {
    const next = { ...this.rowStatuses() };
    next[applicationId] = this.toApplicationStatus(value);
    this.rowStatuses.set(next);
  }

  getRowNotes(applicationId: number): string {
    return this.rowNotes()[applicationId] ?? '';
  }

  onRowNotesChanged(applicationId: number, value: string): void {
    const next = { ...this.rowNotes() };
    next[applicationId] = value;
    this.rowNotes.set(next);
  }

  isRowSubmitting(applicationId: number): boolean {
    return !!this.rowSubmitting()[applicationId];
  }

  isSelected(applicationId: number): boolean {
    return this.selectedApplicationIds().includes(applicationId);
  }

  onSelectionChange(applicationId: number, checked: boolean): void {
    const selected = new Set(this.selectedApplicationIds());
    if (checked) {
      selected.add(applicationId);
    } else {
      selected.delete(applicationId);
    }
    this.selectedApplicationIds.set([...selected]);
  }

  onSelectAllChange(checked: boolean): void {
    if (checked) {
      this.selectedApplicationIds.set(this.applications().map((item) => item.applicationId));
      return;
    }

    this.selectedApplicationIds.set([]);
  }

  applySingleStatusUpdate(applicationId: number): void {
    const nextStatus = this.getRowStatus(applicationId);
    if (!nextStatus || this.isRowSubmitting(applicationId) || this.submitting()) {
      return;
    }

    const previousApplications = this.applications();
    const previousRowStatuses = this.rowStatuses();
    const previousRowNotes = this.rowNotes();

    this.applications.set(
      this.applyOptimisticStatus(previousApplications, [applicationId], nextStatus)
    );
    this.trimTransientStateToVisibleRows();

    this.setRowSubmitting(applicationId, true);
    this.error.set(null);
    this.success.set(null);

    this.applicationsService
      .updateStatus(applicationId, {
        status: nextStatus,
        notes: this.getRowNotes(applicationId).trim() || undefined
      })
      .pipe(finalize(() => this.setRowSubmitting(applicationId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || `Application ${applicationId} updated successfully.`);
        },
        error: (err: unknown) => {
          this.applications.set(previousApplications);
          this.rowStatuses.set(previousRowStatuses);
          this.rowNotes.set(previousRowNotes);
          this.error.set(this.mapError(err, 'Unable to update this application status right now.'));
        }
      });
  }

  openBulkConfirmation(): void {
    const nextStatus = this.bulkStatus();
    if (!nextStatus || !this.hasSelection() || this.submitting()) {
      return;
    }

    this.showConfirmModal.set(true);
  }

  closeBulkConfirmation(): void {
    if (this.submitting()) {
      return;
    }

    this.showConfirmModal.set(false);
  }

  canScheduleInterview(application: CandidateApplicationDto): boolean {
    return !application.hasInterview;
  }

  openDetails(applicationId: number): void {
    if (!Number.isFinite(applicationId) || applicationId <= 0) {
      return;
    }

    void this.router.navigate(['/recruiter/applications', applicationId]);
  }

  openScheduleModal(application: CandidateApplicationDto): void {
    if (!this.canScheduleInterview(application) || this.scheduling()) {
      return;
    }

    const selectedJob = this.jobs().find((job) => job.id === this.selectedJobId());
    const jobTitle = selectedJob?.title || 'Interview';

    this.scheduleApplicationId.set(application.applicationId);
    this.scheduleCandidateName.set(application.candidateName);
    this.scheduleJobTitle.set(jobTitle);
    this.scheduleTitle.set(`${jobTitle} interview`);
    this.scheduleDateTime.set(this.toDateTimeLocal(new Date(Date.now() + 24 * 60 * 60 * 1000)));
    this.scheduleAgentType.set('Mixed');
    this.showScheduleModal.set(true);
  }

  closeScheduleModal(): void {
    if (this.scheduling()) {
      return;
    }

    this.showScheduleModal.set(false);
  }

  onScheduleTitleChanged(value: string): void {
    this.scheduleTitle.set(value);
  }

  onScheduleDateTimeChanged(value: string): void {
    this.scheduleDateTime.set(value);
  }

  onScheduleAgentTypeChanged(value: string): void {
    this.scheduleAgentType.set(this.toInterviewAgentType(value));
  }

  submitScheduleInterview(): void {
    const applicationId = this.scheduleApplicationId();
    if (!applicationId || this.scheduling()) {
      return;
    }

    const scheduledAt = this.toIsoDateTime(this.scheduleDateTime());
    if (!scheduledAt) {
      this.error.set('Select a valid interview date and time before scheduling.');
      return;
    }

    this.scheduling.set(true);
    this.error.set(null);
    this.success.set(null);

    this.interviewsService
      .schedule({
        applicationId,
        scheduledAt,
        agentType: this.scheduleAgentType(),
        interviewTitle: this.scheduleTitle().trim() || undefined
      })
      .pipe(finalize(() => this.scheduling.set(false)))
      .subscribe({
        next: (res) => {
          this.showScheduleModal.set(false);
          this.success.set(res.message || 'Interview session scheduled successfully.');
          this.loadApplications();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to schedule interview right now.'));
        }
      });
  }

  confirmBulkStatusUpdate(): void {
    const nextStatus = this.bulkStatus();
    if (!nextStatus || !this.hasSelection() || this.submitting()) {
      return;
    }

    const selectedIds = this.selectedApplicationIds();
    const previousApplications = this.applications();
    const previousSelection = this.selectedApplicationIds();
    const previousRowStatuses = this.rowStatuses();
    const previousRowNotes = this.rowNotes();

    this.applications.set(this.applyOptimisticStatus(previousApplications, selectedIds, nextStatus));
    this.selectedApplicationIds.set([]);
    this.trimTransientStateToVisibleRows();

    this.submitting.set(true);
    this.error.set(null);
    this.success.set(null);

    this.applicationsService
      .bulkUpdateStatus({
        applicationIds: selectedIds,
        status: nextStatus,
        notes: this.bulkNotes().trim() || undefined
      })
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: (res) => {
          this.showConfirmModal.set(false);
          this.bulkResult.set(res.data);
          const summary = this.describeBulkResult(res.data);
          this.success.set(summary || res.message || 'Application statuses updated.');
          this.bulkNotes.set('');
          this.persistFilterState();
        },
        error: (err: unknown) => {
          this.applications.set(previousApplications);
          this.selectedApplicationIds.set(previousSelection);
          this.rowStatuses.set(previousRowStatuses);
          this.rowNotes.set(previousRowNotes);
          this.error.set(this.mapError(err, 'Unable to apply bulk status update right now.'));
        }
      });
  }

  private extractStatusOptions(options: EnumOptionDto[]): ApplicationStatus[] {
    const parsed = options
      .map((item) => item.name)
      .filter((name): name is ApplicationStatus => FALLBACK_STATUSES.includes(name as ApplicationStatus));

    if (parsed.length === 0) {
      return [...FALLBACK_STATUSES];
    }

    return parsed;
  }

  private syncRowDrafts(items: CandidateApplicationDto[]): void {
    const rowStatuses: Record<number, ApplicationStatus | ''> = {};
    const rowNotes: Record<number, string> = {};
    const currentSubmitting = this.rowSubmitting();
    const nextSubmitting: Record<number, boolean> = {};

    for (const item of items) {
      rowStatuses[item.applicationId] = this.toApplicationStatus(item.status);
      rowNotes[item.applicationId] = '';
      if (currentSubmitting[item.applicationId]) {
        nextSubmitting[item.applicationId] = true;
      }
    }

    this.rowStatuses.set(rowStatuses);
    this.rowNotes.set(rowNotes);
    this.rowSubmitting.set(nextSubmitting);
  }

  private setRowSubmitting(applicationId: number, value: boolean): void {
    const next = { ...this.rowSubmitting() };
    if (value) {
      next[applicationId] = true;
    } else {
      delete next[applicationId];
    }
    this.rowSubmitting.set(next);
  }

  private toApplicationStatus(value: string): ApplicationStatus | '' {
    if (FALLBACK_STATUSES.includes(value as ApplicationStatus)) {
      return value as ApplicationStatus;
    }
    return '';
  }

  private toInterviewAgentType(value: string): InterviewAgentType {
    if (INTERVIEW_AGENT_TYPES.includes(value as InterviewAgentType)) {
      return value as InterviewAgentType;
    }

    return 'Mixed';
  }

  private toDateTimeLocal(value: Date): string {
    const offset = value.getTimezoneOffset();
    const local = new Date(value.getTime() - offset * 60_000);
    return local.toISOString().slice(0, 16);
  }

  private toIsoDateTime(value: string): string | null {
    if (!value.trim()) {
      return null;
    }

    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) {
      return null;
    }

    return parsed.toISOString();
  }

  private describeBulkResult(data: BulkUpdateApplicationStatusResultDto | null): string | null {
    if (!data) {
      return null;
    }

    return `Updated ${data.updatedCount} of ${data.requestedCount} selected applications (${data.skippedCount} skipped).`;
  }

  private applyOptimisticStatus(
    rows: CandidateApplicationDto[],
    applicationIds: number[],
    nextStatus: ApplicationStatus
  ): CandidateApplicationDto[] {
    const selected = new Set(applicationIds);
    const updated = rows.map((row) =>
      selected.has(row.applicationId) ? { ...row, status: nextStatus } : row
    );

    const activeFilter = this.filterStatus();
    if (activeFilter && activeFilter !== nextStatus) {
      return updated.filter((row) => !selected.has(row.applicationId));
    }

    return updated;
  }

  private trimTransientStateToVisibleRows(): void {
    const visibleIds = new Set(this.applications().map((item) => item.applicationId));

    this.selectedApplicationIds.set(
      this.selectedApplicationIds().filter((id) => visibleIds.has(id))
    );

    const nextStatuses: Record<number, ApplicationStatus | ''> = {};
    Object.entries(this.rowStatuses()).forEach(([id, status]) => {
      const numericId = Number(id);
      if (visibleIds.has(numericId)) {
        nextStatuses[numericId] = status;
      }
    });
    this.rowStatuses.set(nextStatuses);

    const nextNotes: Record<number, string> = {};
    Object.entries(this.rowNotes()).forEach(([id, note]) => {
      const numericId = Number(id);
      if (visibleIds.has(numericId)) {
        nextNotes[numericId] = note;
      }
    });
    this.rowNotes.set(nextNotes);

    const nextSubmitting: Record<number, boolean> = {};
    Object.entries(this.rowSubmitting()).forEach(([id, submitting]) => {
      const numericId = Number(id);
      if (visibleIds.has(numericId) && submitting) {
        nextSubmitting[numericId] = true;
      }
    });
    this.rowSubmitting.set(nextSubmitting);
  }

  private restoreFilterState(): void {
    const saved = this.sessionFilters.read<RecruiterApplicationsFilterState>(FILTER_SCOPE, {
      selectedJobId: null,
      filterStatus: '',
      bulkStatus: '',
      bulkNotes: '',
      pageNumber: 1,
      pageSize: PAGE_SIZE_OPTIONS[0]
    });

    this.selectedJobId.set(this.parseOptionalPositiveInt(saved.selectedJobId));
    this.filterStatus.set(this.toApplicationStatus(saved.filterStatus ?? ''));
    this.bulkStatus.set(this.toApplicationStatus(saved.bulkStatus ?? ''));
    this.bulkNotes.set(saved.bulkNotes ?? '');
    this.pageNumber.set(this.parsePositiveInt(saved.pageNumber, 1));
    this.pageSize.set(this.parsePageSize(saved.pageSize));
  }

  private persistFilterState(): void {
    this.sessionFilters.write<RecruiterApplicationsFilterState>(FILTER_SCOPE, {
      selectedJobId: this.selectedJobId(),
      filterStatus: this.filterStatus(),
      bulkStatus: this.bulkStatus(),
      bulkNotes: this.bulkNotes(),
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize()
    });
  }

  private parseOptionalPositiveInt(value: unknown): number | null {
    if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
      return null;
    }

    return value;
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

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
