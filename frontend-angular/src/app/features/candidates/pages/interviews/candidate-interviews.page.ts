import { CommonModule, DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { InterviewsService } from '../../../interviews/interviews.service';
import { InterviewSessionListDto } from '../../../../core/models/interview.model';

const STATUS_OPTIONS = [
  'Draft',
  'Scheduled',
  'Live',
  'InProgress',
  'Completed',
  'Abandoned',
  'ReviewRequired',
  'Cancelled'
];

@Component({
  selector: 'app-candidate-interviews-page',
  imports: [CommonModule, RouterLink, DatePipe, DecimalPipe],
  templateUrl: './candidate-interviews.page.html',
  styleUrls: ['./candidate-interviews.page.css']
})
export class CandidateInterviewsPage {
  private readonly interviewsService = inject(InterviewsService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly statusOptions = signal<string[]>([...STATUS_OPTIONS]);
  readonly statusFilter = signal('');
  readonly interviews = signal<InterviewSessionListDto[]>([]);

  readonly totalSessions = computed(() => this.interviews().length);
  readonly scheduledSessions = computed(
    () =>
      this.interviews().filter((i) => {
        const s = this.normalizeStatus(i.status);
        return s.includes('scheduled') || s.includes('draft');
      }).length
  );
  readonly completedSessions = computed(
    () =>
      this.interviews().filter((i) => this.normalizeStatus(i.status).includes('complet')).length
  );

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.interviewsService
      .getCandidateInterviews({
        pageNumber: 1,
        pageSize: 100,
        sortBy: 'scheduledAt',
        status: this.statusFilter().trim() || undefined
      })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => this.interviews.set(res.data?.items ?? []),
        error: (err: unknown) =>
          this.error.set(this.mapError(err, 'Unable to load interview sessions right now.'))
      });
  }

  onStatusFilterChanged(value: string): void {
    this.statusFilter.set(value);
  }

  applyFilter(): void {
    this.load();
  }

  clearFilter(): void {
    this.statusFilter.set('');
    this.load();
  }

  isScheduled(session: InterviewSessionListDto): boolean {
    const s = this.normalizeStatus(session.status);
    return s.includes('scheduled') || s.includes('draft');
  }

  isLive(session: InterviewSessionListDto): boolean {
    const s = this.normalizeStatus(session.status);
    return s.includes('progress') || s.includes('live') || s.includes('started');
  }

  isCompleted(session: InterviewSessionListDto): boolean {
    return this.normalizeStatus(session.status).includes('complet');
  }

  canViewReport(session: InterviewSessionListDto): boolean {
    return (
      this.isCompleted(session) &&
      session.overallScore !== null &&
      session.overallScore !== undefined
    );
  }

  formatStatus(status: string): string {
    return status.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ').trim();
  }

  getStatusClass(status: string): string {
    const s = this.normalizeStatus(status);
    if (s.includes('complet')) return 'status status--completed';
    if (s.includes('progress') || s.includes('live') || s.includes('started'))
      return 'status status--live';
    if (s.includes('cancel') || s.includes('abandon')) return 'status status--cancelled';
    return 'status status--scheduled';
  }

  private normalizeStatus(status: string): string {
    return (status ?? '').trim().toLowerCase();
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) return err.message;
    return fallback;
  }
}
