import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { InterviewSessionListDto } from '../../../../core/models/interview.model';
import { JobListDto } from '../../../../core/models/job.model';
import { RecruiterDashboardDto } from '../../../../core/models/recruiter.model';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { InterviewsService } from '../../../interviews/interviews.service';
import { JobsService } from '../../../jobs/jobs.service';
import { RecruiterService } from '../../recruiter.service';

@Component({
  selector: 'app-recruiter-dashboard-page',
  imports: [CommonModule, LoadingSpinnerComponent],
  templateUrl: './recruiter-dashboard.page.html',
  styleUrl: './recruiter-dashboard.page.css'
})
export class RecruiterDashboardPage {
  private readonly recruiterService = inject(RecruiterService);
  private readonly jobsService = inject(JobsService);
  private readonly interviewsService = inject(InterviewsService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly dashboard = signal<RecruiterDashboardDto | null>(null);
  readonly jobs = signal<JobListDto[]>([]);
  readonly interviews = signal<InterviewSessionListDto[]>([]);

  readonly activeConversion = computed(() => {
    const data = this.dashboard();
    if (!data || data.totalApplications === 0) {
      return 0;
    }

    return Math.round((data.pendingApplications / data.totalApplications) * 100);
  });

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      dashboard: this.recruiterService.getDashboard().pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      jobs: this.jobsService
        .getMyJobs({ pageNumber: 1, pageSize: 5, sortBy: 'postedAt', sortDescending: true })
        .pipe(
          map((res) => res.data?.items ?? []),
          catchError(() => of([]))
        ),
      interviews: this.interviewsService
        .getRecruiterInterviews({ pageNumber: 1, pageSize: 5, sortBy: 'scheduledAt' })
        .pipe(
          map((res) => res.data?.items ?? []),
          catchError(() => of([]))
        )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ dashboard, jobs, interviews }) => {
          this.dashboard.set(dashboard);
          this.jobs.set(jobs);
          this.interviews.set(interviews);
        },
        error: () => {
          this.error.set('Unable to load recruiter dashboard right now. Please refresh.');
        }
      });
  }
}
