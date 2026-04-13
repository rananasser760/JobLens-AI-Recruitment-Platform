import { CommonModule, DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { JobRecommendationDto } from '../../../../core/models/job.model';
import { JobsService } from '../../../jobs/jobs.service';

@Component({
  selector: 'app-candidate-recommendations-page',
  imports: [CommonModule, RouterLink, DatePipe],
  templateUrl: './candidate-recommendations.page.html',
  styleUrl: './candidate-recommendations.page.css'
})
export class CandidateRecommendationsPage {
  private readonly jobsService = inject(JobsService);

  readonly loading = signal(true);
  readonly refreshing = signal(false);
  readonly error = signal<string | null>(null);

  readonly recommendations = signal<JobRecommendationDto[]>([]);
  readonly lastUpdated = signal<Date | null>(null);

  constructor() {
    this.load(false);
  }

  load(forceRefresh = false): void {
    if (forceRefresh) {
      this.refreshing.set(true);
    } else {
      this.loading.set(true);
    }

    this.error.set(null);

    this.jobsService
      .getCandidateRecommendations(30, forceRefresh)
      .pipe(
        finalize(() => {
          this.loading.set(false);
          this.refreshing.set(false);
        })
      )
      .subscribe({
        next: (res) => {
          this.recommendations.set(res.data ?? []);
          this.lastUpdated.set(new Date());
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load recommendations right now.'));
        }
      });
  }

  refreshFromAi(): void {
    if (this.refreshing()) {
      return;
    }

    this.load(true);
  }

  getScoreTier(score: number | null | undefined): string {
    if (score === null || score === undefined) return 'low';
    if (score >= 80) return 'high';
    if (score >= 60) return 'mid';
    return 'low';
  }

  trackByJobId(_: number, item: JobRecommendationDto): number {
    return item.jobId;
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
