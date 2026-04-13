import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { finalize } from 'rxjs';

import { BackgroundJobDto } from '../../models/admin.model';
import { AdminService } from '../../services/admin.service';

@Component({
  selector: 'app-admin-dashboard-page',
  imports: [CommonModule, ReactiveFormsModule],
  templateUrl: './admin-dashboard.page.html',
  styleUrl: './admin-dashboard.page.css'
})
export class AdminDashboardPage {
  private readonly fb = inject(FormBuilder);
  private readonly adminService = inject(AdminService);

  readonly loading = signal(false);
  readonly acting = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);
  readonly jobs = signal<BackgroundJobDto[]>([]);

  readonly scrapeForm = this.fb.nonNullable.group({
    maxCategories: [5, [Validators.min(1), Validators.max(50)]]
  });

  readonly cleanupForm = this.fb.nonNullable.group({
    staleAfterDays: [45, [Validators.required, Validators.min(1), Validators.max(365)]]
  });

  constructor() {
    this.refresh();
  }

  refresh(): void {
    if (this.loading()) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.adminService
      .getBackgroundJobs()
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          this.jobs.set(res.data ?? []);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load background jobs.'));
        }
      });
  }

  triggerScrape(): void {
    if (this.acting() || this.scrapeForm.invalid) {
      this.scrapeForm.markAllAsTouched();
      return;
    }

    this.acting.set(true);
    this.error.set(null);
    this.success.set(null);

    const maxCategories = this.scrapeForm.controls.maxCategories.value;
    this.adminService
      .triggerScrape({ maxCategories })
      .pipe(finalize(() => this.acting.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Scrape queued.');
          this.refresh();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to queue scraping job.'));
        }
      });
  }

  cleanupStaleJobs(): void {
    if (this.acting() || this.cleanupForm.invalid) {
      this.cleanupForm.markAllAsTouched();
      return;
    }

    this.acting.set(true);
    this.error.set(null);
    this.success.set(null);

    const staleAfterDays = this.cleanupForm.controls.staleAfterDays.value;
    this.adminService
      .cleanupJobs({ staleAfterDays })
      .pipe(finalize(() => this.acting.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Cleanup queued.');
          this.refresh();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to queue cleanup job.'));
        }
      });
  }

  refreshRecommendations(): void {
    if (this.acting()) {
      return;
    }

    this.acting.set(true);
    this.error.set(null);
    this.success.set(null);

    this.adminService
      .refreshRecommendations()
      .pipe(finalize(() => this.acting.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Recommendation refresh queued.');
          this.refresh();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to queue recommendation refresh.'));
        }
      });
  }

  trackByJobId(_: number, item: BackgroundJobDto): number {
    return item.jobId;
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
