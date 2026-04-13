import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { InterviewReportDto } from '../../../../core/models/interview.model';
import { InterviewsService } from '../../../interviews/interviews.service';

@Component({
  selector: 'app-recruiter-interview-report-page',
  imports: [CommonModule, RouterLink],
  templateUrl: './recruiter-interview-report.page.html',
  styleUrl: './recruiter-interview-report.page.css'
})
export class RecruiterInterviewReportPage {
  private readonly route = inject(ActivatedRoute);
  private readonly interviewsService = inject(InterviewsService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly sessionId = signal<number | null>(null);
  readonly report = signal<InterviewReportDto | null>(null);

  readonly eventRows = computed(() =>
    Object.entries(this.report()?.cheatingReport.eventsByType ?? {})
  );

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

  load(): void {
    const id = this.sessionId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.interviewsService
      .getReport(id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          this.report.set(res.data);
          if (!res.data) {
            this.error.set('Interview report is unavailable for this session yet.');
          }
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load interview report right now.'));
        }
      });
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
