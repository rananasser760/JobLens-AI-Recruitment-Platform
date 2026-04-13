import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { JobDto } from '../../../../core/models/job.model';
import { ApplicationsService } from '../../../applications/applications.service';
import { JobsService } from '../../../jobs/jobs.service';
import { ResumeService } from '../../../resumes/resume.service';
import { ResumeDto } from '../../../../core/models/resume.model';

@Component({
  selector: 'app-candidate-job-detail-page',
  imports: [CommonModule, RouterLink],
  templateUrl: './candidate-job-detail.page.html',
  styleUrl: './candidate-job-detail.page.css'
})
export class CandidateJobDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly jobsService = inject(JobsService);
  private readonly applicationsService = inject(ApplicationsService);
  private readonly resumeService = inject(ResumeService);

  readonly loading = signal(true);
  readonly applying = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly jobId = signal<number | null>(null);
  readonly job = signal<JobDto | null>(null);
  readonly hasApplied = signal(false);
  readonly defaultResumeId = signal<number | null>(null);
  readonly coverLetter = signal('');

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
      hasApplied: this.applicationsService.checkIfApplied(id).pipe(
        map((res) => !!res.data?.hasApplied),
        catchError(() => of(false))
      ),
      resumes: this.resumeService.getMyResumes().pipe(
        map((res) => res.data ?? []),
        catchError(() => of([] as ResumeDto[]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ job, hasApplied, resumes }) => {
          this.job.set(job);
          this.hasApplied.set(hasApplied);

          const preferredResume = resumes.find((item) => item.isDefault) ?? resumes[0] ?? null;
          this.defaultResumeId.set(preferredResume?.id ?? null);

          if (!job) {
            this.error.set('Job details are unavailable.');
          }
        },
        error: () => {
          this.error.set('Unable to load this job right now.');
        }
      });
  }

  onCoverLetterChanged(value: string): void {
    this.coverLetter.set(value);
  }

  applyNow(): void {
    const id = this.jobId();
    if (!id || this.applying() || this.hasApplied()) {
      return;
    }

    this.applying.set(true);
    this.error.set(null);
    this.success.set(null);

    const resumeId = this.defaultResumeId();
    if (!resumeId) {
      this.applying.set(false);
      this.error.set('Please upload a resume before applying to jobs.');
      return;
    }

    this.applicationsService
      .apply({
        jobId: id,
        resumeId,
        coverLetter: this.coverLetter().trim() || undefined
      })
      .pipe(finalize(() => this.applying.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Application submitted successfully.');
          this.hasApplied.set(true);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to submit your application right now.'));
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
