import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { finalize } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';

@Component({
  selector: 'app-candidate-layout',
  imports: [CommonModule, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './candidate-layout.component.html',
  styleUrl: './candidate-layout.component.css'
})
export class CandidateLayoutComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly signingOut = signal(false);
  readonly currentUser = this.auth.currentUser;
  readonly displayName = computed(
    () => this.currentUser()?.fullName || this.currentUser()?.username || 'Candidate'
  );

  logout(): void {
    if (this.signingOut()) {
      return;
    }

    this.signingOut.set(true);
    this.auth
      .logout()
      .pipe(finalize(() => this.signingOut.set(false)))
      .subscribe({
        next: () => this.router.navigate(['/auth/login']),
        error: () => {
          this.auth.hardLogout();
          this.router.navigate(['/auth/login']);
        }
      });
  }
}
