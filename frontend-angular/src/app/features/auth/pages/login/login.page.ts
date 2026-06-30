import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { AuthService } from '../../../../core/auth/auth.service';
import { UserRole } from '../../../../core/models/auth.model';

@Component({
  selector: 'app-login-page',
  imports: [CommonModule, ReactiveFormsModule, RouterLink],
  templateUrl: './login.page.html',
  styleUrl: './login.page.css'
})
export class LoginPage {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]]
  });

  submit(): void {
    if (this.form.invalid || this.submitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.error.set(null);

    this.auth
      .login(this.form.getRawValue())
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: (session) => {
          this.routeByRole(session.role);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err));
        }
      });
  }

  private routeByRole(role: UserRole): void {
    if (role === 'Admin') {
      this.router.navigate(['/recruiter/admin']);
      return;
    }

    if (role === 'Recruiter') {
      this.router.navigate(['/recruiter/dashboard']);
      return;
    }
    this.router.navigate(['/candidate/dashboard']);
  }

  private mapError(err: unknown): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }
    return 'Unable to sign in. Please review your credentials and try again.';
  }
}
