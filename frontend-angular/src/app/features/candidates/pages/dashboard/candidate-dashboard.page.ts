import { CommonModule } from '@angular/common';
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
  imports: [CommonModule, RouterLink, LoadingSpinnerComponent],
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

  readonly stats = computed(() => {
    const dashboard = this.dashboard();

    return {
      totalApplications: dashboard?.totalApplications ?? 0,
      pendingReview: dashboard?.activeApplications ?? 0,
      interviewsUpcoming: dashboard?.interviewsScheduled ?? 0,
      strongestMatch: this.recommendedJobs()[0]?.matchScore ?? null
    };
  });

  readonly onboardingChecklist = computed<OnboardingChecklistItem[]>(() => {
    const profile = this.profile();
    const hasProfileBasics = !!profile?.fullName && !!profile?.currentTitle && !!profile?.location;
    const hasSkills = (profile?.skills?.length ?? 0) > 0;
    const hasResume = (profile?.resumes?.length ?? 0) > 0;
    const hasParsedResume = (profile?.resumes ?? []).some((resume) => resume.isParsed);

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

  readonly onboardingCompletedCount = computed(() => this.onboardingChecklist().filter((item) => item.done).length);

  readonly onboardingTotalCount = computed(() => this.onboardingChecklist().length);

  readonly onboardingComplete = computed(
    () => this.onboardingTotalCount() > 0 && this.onboardingCompletedCount() === this.onboardingTotalCount()
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
          this.error.set('Unable to load candidate dashboard right now. Please refresh.');
        }
      });
  }

  private loadRecommendations(): void {
    this.recommendationsLoading.set(true);

    this.jobsService
      .getCandidateRecommendations(4)
      .pipe(
        map((res) => res.data ?? []),
        catchError(() => of<JobRecommendationDto[] | null>(null)),
        finalize(() => this.recommendationsLoading.set(false))
      )
      .subscribe((recommendations) => {
        if (recommendations !== null) {
          this.recommendedJobs.set(recommendations);
        }
      });
  }
}
