import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { CandidateApplicationDto } from '../../../../core/models/application.model';
import { JobDto } from '../../../../core/models/job.model';
import { ApplicationsService } from '../../../applications/applications.service';
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
  private readonly applicationsService = inject(ApplicationsService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly jobId = signal<number | null>(null);
  readonly job = signal<JobDto | null>(null);
  readonly candidates = signal<CandidateApplicationDto[]>([]);

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

  load(): void {
    const id = this.jobId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      job: this.jobsService.getById(id).pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      candidates: this.applicationsService.getRankedCandidates(id).pipe(
        map((res) => res.data ?? []),
        catchError(() => of([] as CandidateApplicationDto[]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ job, candidates }) => {
          this.job.set(job);
          this.candidates.set(candidates);
          if (!job) {
            this.error.set('Job details are unavailable.');
          }
        },
        error: () => {
          this.error.set('Unable to load top candidates right now.');
        }
      });
  }

  trackByApplicationId(_: number, item: CandidateApplicationDto): number {
    return item.applicationId;
  }
}
