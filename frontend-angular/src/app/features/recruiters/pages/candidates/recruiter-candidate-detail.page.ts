import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { CandidateProfileDto } from '../../../../core/models/candidate.model';
import { CandidateService } from '../../../candidates/candidate.service';

@Component({
  selector: 'app-recruiter-candidate-detail-page',
  imports: [CommonModule, RouterLink],
  templateUrl: './recruiter-candidate-detail.page.html',
  styleUrl: './recruiter-candidate-detail.page.css'
})
export class RecruiterCandidateDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly candidateService = inject(CandidateService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly profile = signal<CandidateProfileDto | null>(null);
  readonly candidateId = signal<number | null>(null);

  constructor() {
    this.route.paramMap.subscribe((params) => {
      const rawId = Number(params.get('candidateId'));
      if (!Number.isFinite(rawId) || rawId <= 0) {
        this.loading.set(false);
        this.error.set('Invalid candidate identifier provided.');
        return;
      }

      this.candidateId.set(rawId);
      this.load();
    });
  }

  load(): void {
    const id = this.candidateId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.candidateService
      .getById(id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          this.profile.set(res.data);
          if (!res.data) {
            this.error.set('Candidate profile is unavailable.');
          }
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load candidate profile right now.'));
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
