import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators
} from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { AuthService } from '../../../../core/auth/auth.service';
import { RegisterDto, UserRole } from '../../../../core/models/auth.model';

const passwordMatchValidator: ValidatorFn = (
  control: AbstractControl
): ValidationErrors | null => {
  const password = control.get('password')?.value;
  const confirm = control.get('confirmPassword')?.value;

  if (!password || !confirm) {
    return null;
  }

  return password === confirm ? null : { passwordMismatch: true };
};

@Component({
  selector: 'app-register-page',
  imports: [CommonModule, ReactiveFormsModule, RouterLink],
  templateUrl: './register.page.html',
  styleUrl: './register.page.css'
})
export class RegisterPage {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group(
    {
      role: 'Candidate' as 'Candidate' | 'Recruiter',
      fullName: ['', [Validators.required, Validators.minLength(3)]],
      username: ['', [Validators.required, Validators.minLength(3)]],
      email: ['', [Validators.required, Validators.email]],
      password: ['', [Validators.required, Validators.minLength(8)]],
      confirmPassword: ['', [Validators.required]],
      companyId: ''
    },
    { validators: passwordMatchValidator }
  );

  submit(): void {
    if (this.form.invalid || this.submitting()) {
      this.form.markAllAsTouched();
      return;
    }

    const raw = this.form.getRawValue();
    const payload: RegisterDto = {
      role: raw.role,
      fullName: raw.fullName.trim(),
      username: raw.username.trim(),
      email: raw.email.trim(),
      password: raw.password,
      confirmPassword: raw.confirmPassword
    };

    if (raw.role === 'Recruiter' && raw.companyId) {
      const id = Number(raw.companyId);
      if (!Number.isNaN(id)) {
        payload.companyId = id;
      }
    }

    this.submitting.set(true);
    this.error.set(null);

    this.auth
      .register(payload)
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: (session) => this.routeByRole(session.role),
        error: (err: unknown) => this.error.set(this.mapError(err))
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
    return 'Unable to create account with the submitted details.';
  }
}
