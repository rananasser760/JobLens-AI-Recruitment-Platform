import { CommonModule, DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { ApplicationsService } from '../../../applications/applications.service';
import { ApplicationDto, ApplicationStatus } from '../../../../core/models/application.model';
import { ConfirmDialogComponent } from '../../../../shared/components/confirm-dialog/confirm-dialog.component';

const STATUS_OPTIONS: ApplicationStatus[] = [
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

@Component({
  selector: 'app-candidate-applications-page',
  imports: [CommonModule, RouterLink, DatePipe, ConfirmDialogComponent],
  templateUrl: './candidate-applications.page.html',
  styleUrls: ['./candidate-applications.page.css']
})
export class CandidateApplicationsPage {
  private readonly applicationsService = inject(ApplicationsService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly applications = signal<ApplicationDto[]>([]);
  readonly statusFilter = signal<ApplicationStatus | ''>('');
  readonly statusOptions = signal<ApplicationStatus[]>([...STATUS_OPTIONS]);
  readonly withdrawing = signal<Record<number, boolean>>({});
  readonly confirmWithdrawOpen = signal(false);
  readonly pendingWithdrawApplication = signal<ApplicationDto | null>(null);
  readonly pendingWithdrawBusy = computed(() => {
    const pending = this.pendingWithdrawApplication();
    return pending ? this.isWithdrawing(pending.id) : false;
  });

  readonly totalApplications = computed(() => this.applications().length);
  readonly activeApplications = computed(() =>
    this.applications().filter((item) => !['Rejected', 'Withdrawn', 'Offered'].includes(item.status)).length
  );
  readonly interviewReady = computed(() =>
    this.applications().filter((item) => item.hasInterview).length
  );

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.applicationsService
      .getMyApplications({
        pageNumber: 1,
        pageSize: 120,
        sortBy: 'appliedAt',
        sortDescending: true,
        status: this.statusFilter() || undefined
      })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          this.applications.set(res.data?.items ?? []);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load your applications right now.'));
        }
      });
  }

  onStatusFilterChanged(value: string): void {
    this.statusFilter.set(this.toApplicationStatus(value));
  }

  applyFilter(): void {
    this.load();
  }

  clearFilter(): void {
    this.statusFilter.set('');
    this.load();
  }

  isWithdrawing(applicationId: number): boolean {
    return !!this.withdrawing()[applicationId];
  }

  canWithdraw(application: ApplicationDto): boolean {
    if (this.isWithdrawing(application.id)) {
      return false;
    }

    return !['Withdrawn', 'Rejected', 'Offered'].includes(application.status);
  }

  requestWithdraw(application: ApplicationDto): void {
    if (!this.canWithdraw(application)) {
      return;
    }

    this.pendingWithdrawApplication.set(application);
    this.confirmWithdrawOpen.set(true);
  }

  cancelWithdraw(): void {
    const pending = this.pendingWithdrawApplication();
    if (pending && this.isWithdrawing(pending.id)) {
      return;
    }

    this.pendingWithdrawApplication.set(null);
    this.confirmWithdrawOpen.set(false);
  }

  confirmWithdraw(): void {
    const application = this.pendingWithdrawApplication();
    if (!application || !this.canWithdraw(application)) {
      this.cancelWithdraw();
      return;
    }

    this.confirmWithdrawOpen.set(false);
    this.pendingWithdrawApplication.set(null);

    this.setWithdrawing(application.id, true);
    this.error.set(null);
    this.success.set(null);

    this.applicationsService
      .withdraw(application.id)
      .pipe(finalize(() => this.setWithdrawing(application.id, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Application withdrawn successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to withdraw this application right now.'));
        }
      });
  }

  formatStatus(status: string): string {
    return status
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/_/g, ' ')
      .trim();
  }

  getStatusClass(status: string): string {
    if (status === 'Offered') {
      return 'status status--hired';
    }

    if (status === 'Rejected' || status === 'Withdrawn') {
      return 'status status--closed';
    }

    if (status === 'InterviewScheduled' || status === 'InterviewCompleted') {
      return 'status status--interview';
    }

    return 'status status--active';
  }

  private setWithdrawing(applicationId: number, value: boolean): void {
    const next = { ...this.withdrawing() };
    if (value) {
      next[applicationId] = true;
    } else {
      delete next[applicationId];
    }
    this.withdrawing.set(next);
  }

  private toApplicationStatus(value: string): ApplicationStatus | '' {
    if (STATUS_OPTIONS.includes(value as ApplicationStatus)) {
      return value as ApplicationStatus;
    }

    return '';
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
