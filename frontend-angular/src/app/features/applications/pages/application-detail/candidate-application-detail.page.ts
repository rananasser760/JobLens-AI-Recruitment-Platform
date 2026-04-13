import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { ApplicationDto } from '../../../../core/models/application.model';
import { ApplicationsService } from '../../applications.service';
import { ConfirmDialogComponent } from '../../../../shared/components/confirm-dialog/confirm-dialog.component';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';

@Component({
  selector: 'app-candidate-application-detail-page',
  imports: [CommonModule, RouterLink, ConfirmDialogComponent, LoadingSpinnerComponent],
  templateUrl: './candidate-application-detail.page.html',
  styleUrl: './candidate-application-detail.page.css'
})
export class CandidateApplicationDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly applicationsService = inject(ApplicationsService);

  readonly loading = signal(true);
  readonly withdrawing = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly applicationId = signal<number | null>(null);
  readonly application = signal<ApplicationDto | null>(null);
  readonly confirmWithdrawOpen = signal(false);

  readonly backLink = '/candidate/applications';

  readonly canWithdraw = computed(() => {
    const item = this.application();
    if (!item) {
      return false;
    }

    return !['Withdrawn', 'Rejected', 'Offered'].includes(item.status);
  });

  constructor() {
    this.route.paramMap.subscribe((params) => {
      const rawId = Number(params.get('applicationId'));
      if (!Number.isFinite(rawId) || rawId <= 0) {
        this.error.set('Invalid application identifier provided.');
        this.loading.set(false);
        return;
      }

      this.applicationId.set(rawId);
      this.load();
    });
  }

  load(): void {
    const id = this.applicationId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.applicationsService
      .getById(id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          const item = res.data;
          this.application.set(item ?? null);

          if (!item) {
            this.error.set('Application details are unavailable.');
          }
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load application details right now.'));
        }
      });
  }

  withdraw(): void {
    if (!this.application() || !this.canWithdraw() || this.withdrawing()) {
      return;
    }

    this.confirmWithdrawOpen.set(true);
  }

  cancelWithdrawConfirmation(): void {
    if (this.withdrawing()) {
      return;
    }

    this.confirmWithdrawOpen.set(false);
  }

  confirmWithdraw(): void {
    const item = this.application();
    if (!item || !this.canWithdraw() || this.withdrawing()) {
      this.cancelWithdrawConfirmation();
      return;
    }

    this.confirmWithdrawOpen.set(false);

    this.withdrawing.set(true);
    this.error.set(null);
    this.success.set(null);

    this.applicationsService
      .withdraw(item.id)
      .pipe(finalize(() => this.withdrawing.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Application withdrawn successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to withdraw this application right now.'));
        }
      });
  }

  formatStatus(status: string): string {
    return status
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/_/g, ' ')
      .trim();
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}