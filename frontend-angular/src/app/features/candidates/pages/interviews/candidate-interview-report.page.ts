import { CommonModule, DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { InterviewsService } from '../../../interviews/interviews.service';
import { InterviewReportDto } from '../../../../core/models/interview.model';

@Component({
  selector: 'app-candidate-interview-report-page',
  imports: [CommonModule, RouterLink, DatePipe, DecimalPipe],
  templateUrl: './candidate-interview-report.page.html',
  styleUrls: ['./candidate-interview-report.page.css']
})
export class CandidateInterviewReportPage {
  private readonly route = inject(ActivatedRoute);
  private readonly interviewsService = inject(InterviewsService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly sessionId = signal<number | null>(null);
  readonly report = signal<InterviewReportDto | null>(null);

  readonly eventRows = computed(() =>
    Object.entries(this.report()?.cheatingReport.eventsByType ?? {})
  );

  readonly avgScore = computed(() => {
    const scores = (this.report()?.questionScores ?? [])
      .map((q) => q.score)
      .filter((s): s is number => s !== null && s !== undefined);
    if (!scores.length) return null;
    return Math.round(scores.reduce((a, b) => a + b, 0) / scores.length);
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

  load(): void {
    const id = this.sessionId();
    if (!id) return;

    this.loading.set(true);
    this.error.set(null);

    this.interviewsService
      .getReport(id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          this.report.set(res.data);
          if (!res.data) {
            this.error.set(
              'The interview report is not yet available. It may still be processing.'
            );
          }
        },
        error: (err: unknown) =>
          this.error.set(this.mapError(err, 'Unable to load interview report right now.'))
      });
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) return err.message;
    return fallback;
  }
}
