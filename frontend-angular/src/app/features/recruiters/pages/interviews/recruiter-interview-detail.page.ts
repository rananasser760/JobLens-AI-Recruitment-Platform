import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import {
  CheatingEventDto,
  InterviewQuestionDto,
  InterviewSessionDto
} from '../../../../core/models/interview.model';
import { InterviewsService } from '../../../interviews/interviews.service';

@Component({
  selector: 'app-recruiter-interview-detail-page',
  imports: [CommonModule, RouterLink],
  templateUrl: './recruiter-interview-detail.page.html',
  styleUrl: './recruiter-interview-detail.page.css'
})
export class RecruiterInterviewDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly interviewsService = inject(InterviewsService);

  readonly loading = signal(true);
  readonly cancelling = signal(false);
  readonly rescheduling = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly showRescheduleModal = signal(false);
  readonly rescheduleDateTime = signal('');

  readonly sessionId = signal<number | null>(null);
  readonly session = signal<InterviewSessionDto | null>(null);
  readonly questions = signal<InterviewQuestionDto[]>([]);
  readonly cheatingEvents = signal<CheatingEventDto[]>([]);

  readonly answeredCount = computed(
    () => this.questions().filter((item) => item.isAnswered).length
  );
  readonly totalCount = computed(() => this.questions().length || this.session()?.totalQuestions || 0);
  readonly sortedEvents = computed(() => {
    const rows = [...this.cheatingEvents()];
    rows.sort((left, right) => {
      const leftTs = Date.parse(left.detectedAt);
      const rightTs = Date.parse(right.detectedAt);
      return rightTs - leftTs;
    });
    return rows;
  });

  constructor() {
    this.route.paramMap.subscribe((params) => {
      const rawId = Number(params.get('sessionId'));
      if (!Number.isFinite(rawId) || rawId <= 0) {
        this.loading.set(false);
        this.error.set('Invalid interview session identifier provided.');
        return;
      }

      this.sessionId.set(rawId);
      this.load();
    });
  }

  canManageSchedule(): boolean {
    const status = this.session()?.status?.trim().toLowerCase() ?? '';
    return status === 'scheduled' || status === 'draft';
  }

  cancelInterview(): void {
    const id = this.sessionId();
    if (!id || this.cancelling() || !this.canManageSchedule()) {
      return;
    }

    const reason = typeof window === 'undefined'
      ? undefined
      : window.prompt('Optional cancellation reason', 'Cancelled by recruiter') ?? undefined;

    this.cancelling.set(true);
    this.error.set(null);
    this.success.set(null);

    this.interviewsService
      .cancelInterview(id, reason)
      .pipe(finalize(() => this.cancelling.set(false)))
      .subscribe({
        next: () => {
          this.success.set('Interview cancelled successfully.');
          this.load();
        },
        error: () => {
          this.error.set('Unable to cancel this interview right now.');
        }
      });
  }

  rescheduleInterview(): void {
    if (this.rescheduling() || !this.canManageSchedule()) {
      return;
    }

    const current = this.session()?.scheduledAt
      ? this.toDateTimeLocal(this.session()!.scheduledAt!)
      : this.toDateTimeLocal(new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString());

    this.rescheduleDateTime.set(current);
    this.showRescheduleModal.set(true);
  }

  closeRescheduleModal(): void {
    if (this.rescheduling()) {
      return;
    }

    this.showRescheduleModal.set(false);
  }

  onRescheduleDateTimeChanged(value: string): void {
    this.rescheduleDateTime.set(value);
  }

  submitRescheduleInterview(): void {
    const id = this.sessionId();
    if (!id || this.rescheduling() || !this.canManageSchedule()) {
      return;
    }

    const entered = this.rescheduleDateTime().trim();
    if (!entered) {
      this.error.set('Please select a valid future date/time.');
      return;
    }

    const date = new Date(entered);
    if (Number.isNaN(date.getTime()) || date.getTime() <= Date.now()) {
      this.error.set('Please enter a valid future date/time.');
      return;
    }

    this.rescheduling.set(true);
    this.error.set(null);
    this.success.set(null);

    this.interviewsService
      .rescheduleInterview(id, date.toISOString())
      .pipe(finalize(() => this.rescheduling.set(false)))
      .subscribe({
        next: () => {
          this.showRescheduleModal.set(false);
          this.success.set('Interview rescheduled successfully.');
          this.load();
        },
        error: () => {
          this.error.set('Unable to reschedule this interview right now.');
        }
      });
  }

  load(): void {
    const id = this.sessionId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      session: this.interviewsService.getSession(id).pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      questions: this.interviewsService.getQuestions(id).pipe(
        map((res) => res.data ?? []),
        catchError(() => of([] as InterviewQuestionDto[]))
      ),
      cheatingEvents: this.interviewsService.getCheatingEvents(id).pipe(
        map((res) => res.data ?? []),
        catchError(() => of([] as CheatingEventDto[]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ session, questions, cheatingEvents }) => {
          this.session.set(session);
          this.questions.set(questions);
          this.cheatingEvents.set(cheatingEvents);

          if (!session) {
            this.error.set('Interview session details are unavailable.');
          }
        },
        error: () => {
          this.error.set('Unable to load interview details right now.');
        }
      });
  }

  formatStatus(status: string | null | undefined): string {
    if (!status) {
      return 'Unknown';
    }

    return status
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/_/g, ' ')
      .trim();
  }

  private toDateTimeLocal(value: string): string {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return '';
    }

    const offset = date.getTimezoneOffset();
    const local = new Date(date.getTime() - offset * 60_000);
    return local.toISOString().slice(0, 16);
  }
}
