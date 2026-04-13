import { CommonModule, DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { CandidateService } from '../../candidate.service';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import {
  CandidateDashboardDto,
  CandidateProfileDto,
  CandidateRecentApplicationDto,
  CandidateUpcomingInterviewDto
} from '../../../../core/models/candidate.model';
import { JobRecommendationDto } from '../../../../core/models/job.model';
import { JobsService } from '../../../jobs/jobs.service';

interface OnboardingChecklistItem {
  key: 'profile' | 'skills' | 'resume' | 'resumeParsing';
  label: string;
  description: string;
  done: boolean;
}

@Component({
  selector: 'app-candidate-dashboard-page',
  imports: [CommonModule, RouterLink, DatePipe, LoadingSpinnerComponent],
  templateUrl: './candidate-dashboard.page.html',
  styleUrl: './candidate-dashboard.page.css'
})
export class CandidateDashboardPage {
  private readonly candidateService = inject(CandidateService);
  private readonly jobsService = inject(JobsService);

  readonly loading = signal(true);
  readonly recommendationsLoading = signal(false);
  readonly error = signal<string | null>(null);

  readonly dashboard = signal<CandidateDashboardDto | null>(null);
  readonly profile = signal<CandidateProfileDto | null>(null);
  readonly recommendedJobs = signal<JobRecommendationDto[]>([]);
  readonly applications = signal<CandidateRecentApplicationDto[]>([]);
  readonly interviews = signal<CandidateUpcomingInterviewDto[]>([]);

  readonly atsScore = computed(() => this.dashboard()?.highestAtsScore ?? 0);

  readonly greeting = computed(() => {
    const h = new Date().getHours();
    if (h < 12) return 'morning';
    if (h < 17) return 'afternoon';
    return 'evening';
  });

  readonly stats = computed(() => {
    const d = this.dashboard();
    return {
      totalApplications: d?.totalApplications ?? 0,
      pendingReview: d?.activeApplications ?? 0,
      interviewsUpcoming: d?.interviewsScheduled ?? 0,
      strongestMatch: this.recommendedJobs()[0]?.matchScore ?? null
    };
  });

  readonly onboardingChecklist = computed<OnboardingChecklistItem[]>(() => {
    const p = this.profile();
    const hasProfileBasics = !!p?.fullName && !!p?.currentTitle && !!p?.location;
    const hasSkills = (p?.skills?.length ?? 0) > 0;
    const hasResume = (p?.resumes?.length ?? 0) > 0;
    const hasParsedResume = (p?.resumes ?? []).some((r) => r.isParsed);

    return [
      {
        key: 'profile',
        label: 'Complete profile basics',
        description: 'Add your full name, current title, and location so recruiters can discover you.',
        done: hasProfileBasics
      },
      {
        key: 'skills',
        label: 'Add at least one skill',
        description: 'Skills improve matching quality and recommendation relevance.',
        done: hasSkills
      },
      {
        key: 'resume',
        label: 'Upload your resume',
        description: 'A resume is required before you can submit applications.',
        done: hasResume
      },
      {
        key: 'resumeParsing',
        label: 'Parse your resume insights',
        description: 'Parsing generates ATS optimization insights and better AI matching.',
        done: hasParsedResume
      }
    ];
  });

  readonly onboardingCompletedCount = computed(
    () => this.onboardingChecklist().filter((i) => i.done).length
  );
  readonly onboardingTotalCount = computed(() => this.onboardingChecklist().length);
  readonly onboardingComplete = computed(
    () =>
      this.onboardingTotalCount() > 0 &&
      this.onboardingCompletedCount() === this.onboardingTotalCount()
  );

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.loadRecommendations();

    forkJoin({
      profile: this.candidateService.getProfile().pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      dashboard: this.candidateService.getDashboard().pipe(
        map((res) => res.data),
        catchError(() => of(null))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ profile, dashboard }) => {
          this.profile.set(profile);
          this.dashboard.set(dashboard);
          this.applications.set(dashboard?.recentApplications ?? []);
          this.interviews.set(dashboard?.upcomingInterviews ?? []);
        },
        error: () => {
          this.error.set('Unable to load your dashboard right now. Please refresh.');
        }
      });
  }

  getMatchTier(score: number | null | undefined): string {
    if (score === null || score === undefined) return 'unknown';
    if (score >= 80) return 'high';
    if (score >= 60) return 'mid';
    return 'low';
  }

  normalizeStatus(status: string | null | undefined): string {
    return (status ?? '').trim().toLowerCase();
  }

  private loadRecommendations(): void {
    this.recommendationsLoading.set(true);
    this.jobsService
      .getCandidateRecommendations(4)
      .pipe(
        map((res) => res.data ?? []),
        catchError(() => of<JobRecommendationDto[]>([])),
        finalize(() => this.recommendationsLoading.set(false))
      )
      .subscribe((recs) => this.recommendedJobs.set(recs));
  }
}
