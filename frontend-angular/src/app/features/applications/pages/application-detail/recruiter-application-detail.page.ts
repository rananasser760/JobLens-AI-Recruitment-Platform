import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import {
  ApplicationDto,
  ApplicationStatus,
  UpdateApplicationStatusDto
} from '../../../../core/models/application.model';
import { InterviewAgentType } from '../../../../core/models/interview.model';
import { ApplicationsService } from '../../applications.service';
import { InterviewsService } from '../../../interviews/interviews.service';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';

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

const INTERVIEW_AGENT_TYPES: InterviewAgentType[] = ['Technical', 'Behavioral', 'Mixed'];

@Component({
  selector: 'app-recruiter-application-detail-page',
  imports: [CommonModule, RouterLink, LoadingSpinnerComponent],
  templateUrl: './recruiter-application-detail.page.html',
  styleUrl: './recruiter-application-detail.page.css'
})
export class RecruiterApplicationDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly applicationsService = inject(ApplicationsService);
  private readonly interviewsService = inject(InterviewsService);

  readonly loading = signal(true);
  readonly savingStatus = signal(false);
  readonly scheduling = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly applicationId = signal<number | null>(null);
  readonly application = signal<ApplicationDto | null>(null);

  readonly statusOptions = signal<ApplicationStatus[]>([...STATUS_OPTIONS]);
  readonly statusDraft = signal<ApplicationStatus | ''>('');
  readonly notesDraft = signal('');

  readonly showSchedulePanel = signal(false);
  readonly scheduleDateTime = signal('');
  readonly scheduleTitle = signal('');
  readonly scheduleAgentType = signal<InterviewAgentType>('Mixed');
  readonly scheduleAgentTypes = signal<InterviewAgentType[]>([...INTERVIEW_AGENT_TYPES]);

  readonly backLink = '/recruiter/applications';

  constructor() {
    this.route.paramMap.subscribe((params) => {
      const rawId = Number(params.get('applicationId'));
      if (!Number.isFinite(rawId) || rawId <= 0) {
        this.error.set('Invalid application identifier provided.');
        this.loading.set(false);
        return;
      }

      this.applicationId.set(rawId);
      this.load();
    });
  }

  load(): void {
    const id = this.applicationId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.applicationsService
      .getById(id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          const item = res.data;
          this.application.set(item ?? null);

          if (!item) {
            this.error.set('Application details are unavailable.');
            return;
          }

          this.statusDraft.set(this.toApplicationStatus(item.status));
          this.notesDraft.set(item.recruiterNotes ?? '');
          this.scheduleTitle.set(`${item.jobTitle} interview`);
          this.scheduleDateTime.set(this.toDateTimeLocal(new Date(Date.now() + 24 * 60 * 60 * 1000)));
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load application details right now.'));
        }
      });
  }

  onStatusChanged(value: string): void {
    this.statusDraft.set(this.toApplicationStatus(value));
  }

  onNotesChanged(value: string): void {
    this.notesDraft.set(value);
  }

  saveStatus(): void {
    const id = this.applicationId();
    const nextStatus = this.statusDraft();

    if (!id || !nextStatus || this.savingStatus()) {
      return;
    }

    const payload: UpdateApplicationStatusDto = {
      status: nextStatus,
      notes: this.notesDraft().trim() || undefined
    };

    this.savingStatus.set(true);
    this.error.set(null);
    this.success.set(null);

    this.applicationsService
      .updateStatus(id, payload)
      .pipe(finalize(() => this.savingStatus.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Application status updated successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to update application status right now.'));
        }
      });
  }

  toggleSchedulePanel(): void {
    this.showSchedulePanel.set(!this.showSchedulePanel());
  }

  onScheduleDateTimeChanged(value: string): void {
    this.scheduleDateTime.set(value);
  }

  onScheduleTitleChanged(value: string): void {
    this.scheduleTitle.set(value);
  }

  onScheduleAgentTypeChanged(value: string): void {
    if (INTERVIEW_AGENT_TYPES.includes(value as InterviewAgentType)) {
      this.scheduleAgentType.set(value as InterviewAgentType);
    }
  }

  scheduleInterview(): void {
    const item = this.application();
    if (!item || this.scheduling() || item.hasInterview) {
      return;
    }

    const scheduledAt = this.toIsoDateTime(this.scheduleDateTime());
    if (!scheduledAt) {
      this.error.set('Select a valid interview date and time.');
      return;
    }

    this.scheduling.set(true);
    this.error.set(null);
    this.success.set(null);

    this.interviewsService
      .schedule({
        applicationId: item.id,
        scheduledAt,
        interviewTitle: this.scheduleTitle().trim() || undefined,
        agentType: this.scheduleAgentType()
      })
      .pipe(finalize(() => this.scheduling.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Interview scheduled successfully.');
          this.showSchedulePanel.set(false);
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to schedule interview right now.'));
        }
      });
  }

  formatStatus(status: string): string {
    return status
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/_/g, ' ')
      .trim();
  }

  private toApplicationStatus(value: string): ApplicationStatus | '' {
    if (STATUS_OPTIONS.includes(value as ApplicationStatus)) {
      return value as ApplicationStatus;
    }

    return '';
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

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}