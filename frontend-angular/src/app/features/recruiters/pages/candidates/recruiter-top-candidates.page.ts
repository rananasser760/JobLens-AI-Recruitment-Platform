import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { CandidateRecommendationDto, JobDto } from '../../../../core/models/job.model';
import { JobsService } from '../../../jobs/jobs.service';

@Component({
  selector: 'app-recruiter-top-candidates-page',
  imports: [CommonModule, RouterLink],
  templateUrl: './recruiter-top-candidates.page.html',
  styleUrl: './recruiter-top-candidates.page.css'
})
export class RecruiterTopCandidatesPage {
  private readonly route = inject(ActivatedRoute);
  private readonly jobsService = inject(JobsService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly recommendationMessage = signal<string | null>(null);

  readonly jobId = signal<number | null>(null);
  readonly job = signal<JobDto | null>(null);
  readonly candidates = signal<CandidateRecommendationDto[]>([]);

  constructor() {
    this.route.paramMap.subscribe((params) => {
      const rawId = Number(params.get('jobId'));
      if (!Number.isFinite(rawId) || rawId <= 0) {
        this.loading.set(false);
        this.error.set('Invalid job identifier provided.');
        return;
      }

      this.jobId.set(rawId);
      this.load();
    });
  }

  load(forceRefresh = false): void {
    const id = this.jobId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);
    this.recommendationMessage.set(null);

    forkJoin({
      job: this.jobsService.getById(id).pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      recommendations: this.jobsService.getCandidateRecommendationsForJob(id, 20, forceRefresh).pipe(
        map((res) => ({
          candidates: res.data ?? [],
          message: res.message ?? null
        })),
        catchError(() =>
          of({
            candidates: [] as CandidateRecommendationDto[],
            message: 'Unable to load AI recommendations right now.'
          })
        )
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ job, recommendations }) => {
          this.job.set(job);
          this.candidates.set(recommendations.candidates);

          if (recommendations.candidates.length === 0 && recommendations.message) {
            this.recommendationMessage.set(recommendations.message);
          }

          if (!job) {
            this.error.set('Job details are unavailable.');
          }
        },
        error: () => {
          this.error.set('Unable to load top candidates right now.');
        }
      });
  }

  trackByCandidateId(_: number, item: CandidateRecommendationDto): number {
    return item.candidateId;
  }
}
