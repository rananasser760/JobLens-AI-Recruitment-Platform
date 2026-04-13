import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { catchError, finalize, forkJoin, map, Observable, of, switchMap, timer } from 'rxjs';

import { ScrapedJobDto } from '../../../../core/models/job.model';
import {
  BackgroundJobDto,
  ScrapingDiagnosticsDto,
  ScrapingStatusDto
} from '../../models/admin.model';
import { AdminService } from '../../services/admin.service';

interface AdminDashboardSnapshot {
  backgroundJobs: BackgroundJobDto[];
  scrapingStatus: ScrapingStatusDto | null;
  diagnostics: ScrapingDiagnosticsDto | null;
  scrapedJobs: ScrapedJobDto[];
}

@Component({
  selector: 'app-admin-dashboard-page',
  imports: [CommonModule, ReactiveFormsModule],
  templateUrl: './admin-dashboard.page.html',
  styleUrl: './admin-dashboard.page.css'
})
export class AdminDashboardPage {
  private readonly fb = inject(FormBuilder);
  private readonly adminService = inject(AdminService);
  private readonly destroyRef = inject(DestroyRef);
  private wasScrapeRunning = false;

  readonly loading = signal(false);
  readonly monitorLoading = signal(false);
  readonly acting = signal(false);
  readonly error = signal<string | null>(null);
  readonly monitorError = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly jobs = signal<BackgroundJobDto[]>([]);
  readonly scrapingStatus = signal<ScrapingStatusDto | null>(null);
  readonly diagnostics = signal<ScrapingDiagnosticsDto | null>(null);
  readonly scrapedJobs = signal<ScrapedJobDto[]>([]);
  readonly lastSyncAt = signal<Date | null>(null);

  readonly scrapeForm = this.fb.nonNullable.group({
    maxCategories: [5, [Validators.min(1), Validators.max(50)]]
  });

  readonly cleanupForm = this.fb.nonNullable.group({
    staleAfterDays: [45, [Validators.required, Validators.min(1), Validators.max(365)]]
  });

  constructor() {
    this.refresh(true);

    timer(8000, 8000)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        switchMap(() => this.loadSnapshot$(false))
      )
      .subscribe((snapshot) => {
        this.applySnapshot(snapshot);
      });
  }

  refresh(showLoading = true): void {
    if (this.loading()) {
      return;
    }

    this.loading.set(showLoading);
    this.monitorLoading.set(showLoading);
    this.error.set(null);
    this.monitorError.set(null);

    this.loadSnapshot$(true)
      .pipe(
        finalize(() => {
          this.loading.set(false);
          this.monitorLoading.set(false);
        })
      )
      .subscribe((snapshot) => {
        this.applySnapshot(snapshot);
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
          this.refresh(true);
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
          this.refresh(true);
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
          this.refresh(true);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to queue recommendation refresh.'));
        }
      });
  }

  trackByScrapedId(_: number, item: ScrapedJobDto): string {
    return item.externalJobId;
  }

  isScrapeRunning(): boolean {
    const statusRunning = this.scrapingStatus()?.running;
    if (typeof statusRunning === 'boolean') {
      return statusRunning;
    }

    const runtime = this.diagnostics()?.scrapeStats?.running;
    if (typeof runtime === 'boolean') {
      return runtime;
    }

    return this.scrapingStatus()?.running === true;
  }

  scrapingStateLabel(): string {
    const phase = this.scrapingStatus()?.phase;
    if (phase) {
      return phase;
    }

    const detailed = this.diagnostics()?.scrapeStats?.status;
    if (detailed) {
      return detailed;
    }

    return this.scrapingStatus()?.lastStatus || 'Unknown';
  }

  scrapingProgressPercent(): number {
    const explicit = this.scrapingStatus()?.progressPercent;
    if (typeof explicit === 'number' && Number.isFinite(explicit)) {
      return Math.max(0, Math.min(100, Math.round(explicit)));
    }

    if (this.isScrapeRunning()) {
      return 35;
    }

    return this.scrapingStateLabel().toLowerCase() === 'completed' ? 100 : 0;
  }

  scrapingProgressMessage(): string {
    const explicit = this.scrapingStatus()?.message?.trim();
    if (explicit) {
      return explicit;
    }

    const phase = (this.scrapingStatus()?.phase || '').toLowerCase();
    if (phase === 'scraping') {
      return 'Collecting jobs from external sources.';
    }

    if (phase === 'persisting') {
      return 'Saving scraped jobs and metadata into gateway storage.';
    }

    if (phase === 'completed') {
      return 'Scrape completed. Latest scraped jobs are listed below.';
    }

    if (phase === 'failed') {
      return 'Scrape failed. Check the background jobs table for error details.';
    }

    return this.isScrapeRunning() ? 'Scrape is running...' : 'No scrape running right now.';
  }

  scrapingProgressCounters(): string {
    const processed = this.scrapingStatus()?.processedJobs;
    const total = this.scrapingStatus()?.totalJobs;
    const inserted = this.scrapingStatus()?.insertedJobs;
    const updated = this.scrapingStatus()?.updatedJobs;
    const categories = this.scrapingStatus()?.processedCategories;

    const counters: string[] = [];
    if (typeof processed === 'number' && typeof total === 'number' && total > 0) {
      counters.push(`${processed}/${total} jobs processed`);
    }

    if (typeof inserted === 'number') {
      counters.push(`${inserted} inserted`);
    }

    if (typeof updated === 'number') {
      counters.push(`${updated} updated`);
    }

    if (typeof categories === 'number' && categories > 0) {
      counters.push(`${categories} categories processed`);
    }

    return counters.length > 0 ? counters.join(' | ') : 'Waiting for run details...';
  }

  asPercent(value: number | undefined): string {
    return typeof value === 'number' ? `${value.toFixed(2)}%` : '--';
  }

  nonEgyptOutliers() {
    return this.diagnostics()?.topNonEgyptLocations ?? [];
  }

  formatLocation(job: ScrapedJobDto): string {
    const location = job.location?.trim();
    if (location) {
      return location;
    }

    const city = job.city?.trim();
    const country = job.country?.trim();
    if (city && country) {
      return `${city}, ${country}`;
    }

    return city || country || '-';
  }

  private loadSnapshot$(allowErrorBanner: boolean): Observable<AdminDashboardSnapshot> {
    return forkJoin({
      backgroundJobs: this.adminService.getBackgroundJobs().pipe(
        map((res) => res.data ?? []),
        catchError((err: unknown) => {
          if (allowErrorBanner) {
            this.error.set(this.mapError(err, 'Unable to load background jobs.'));
          }
          return of(this.jobs());
        })
      ),
      scrapingStatus: this.adminService.getScrapingStatus().pipe(
        map((res) => res.data),
        catchError((err: unknown) => {
          if (allowErrorBanner) {
            this.monitorError.set(this.mapError(err, 'Unable to load scraping status.'));
          }
          return of(this.scrapingStatus());
        })
      ),
      diagnostics: this.adminService.getScrapingDiagnostics().pipe(
        map((res) => res.data),
        catchError((err: unknown) => {
          if (allowErrorBanner) {
            this.monitorError.set(this.mapError(err, 'Unable to load scraping diagnostics.'));
          }
          return of(this.diagnostics());
        })
      ),
      scrapedJobs: this.adminService.getScrapedJobs(12).pipe(
        map((res) => res.data ?? []),
        catchError((err: unknown) => {
          if (allowErrorBanner) {
            this.monitorError.set(this.mapError(err, 'Unable to load recent scraped jobs.'));
          }
          return of(this.scrapedJobs());
        })
      )
    });
  }

  private applySnapshot(snapshot: AdminDashboardSnapshot): void {
    const wasRunning = this.wasScrapeRunning;

    this.jobs.set(snapshot.backgroundJobs);
    this.scrapingStatus.set(snapshot.scrapingStatus);
    this.diagnostics.set(snapshot.diagnostics);
    this.scrapedJobs.set(snapshot.scrapedJobs);
    this.lastSyncAt.set(new Date());

    const runningNow = this.resolveSnapshotRunning(snapshot);
    this.wasScrapeRunning = runningNow;

    if (wasRunning && !runningNow) {
      const status = (snapshot.scrapingStatus?.lastStatus || '').toLowerCase();
      if (status === 'completed') {
        const processed = snapshot.scrapingStatus?.processedJobs ?? snapshot.scrapingStatus?.totalJobs ?? 0;
        this.success.set(`Scrape completed. Processed ${processed} jobs and refreshed latest results.`);
      } else if (status === 'failed' && !this.error()) {
        this.error.set(snapshot.scrapingStatus?.message || 'Scrape failed. Check background jobs for details.');
      }
    }
  }

  private resolveSnapshotRunning(snapshot: AdminDashboardSnapshot): boolean {
    const statusRunning = snapshot.scrapingStatus?.running;
    if (typeof statusRunning === 'boolean') {
      return statusRunning;
    }

    const runtimeRunning = snapshot.diagnostics?.scrapeStats?.running;
    if (typeof runtimeRunning === 'boolean') {
      return runtimeRunning;
    }

    return false;
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
