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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { AuthService } from '../../../../core/auth/auth.service';
import { ResetPasswordDto } from '../../../../core/models/auth.model';

const passwordMatchValidator: ValidatorFn = (
  control: AbstractControl
): ValidationErrors | null => {
  const password = control.get('newPassword')?.value;
  const confirm = control.get('confirmNewPassword')?.value;

  if (!password || !confirm) {
    return null;
  }

  return password === confirm ? null : { passwordMismatch: true };
};

@Component({
  selector: 'app-reset-password-page',
  imports: [CommonModule, ReactiveFormsModule, RouterLink],
  templateUrl: './reset-password.page.html',
  styleUrl: './reset-password.page.css'
})
export class ResetPasswordPage {
  private readonly fb = inject(FormBuilder);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  readonly submitting = signal(false);
  readonly success = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group(
    {
      email: ['', [Validators.required, Validators.email]],
      token: ['', [Validators.required]],
      newPassword: ['', [Validators.required, Validators.minLength(8)]],
      confirmNewPassword: ['', [Validators.required]]
    },
    { validators: passwordMatchValidator }
  );

  constructor() {
    this.route.queryParamMap.subscribe((params) => {
      const email = params.get('email') ?? '';
      const token = params.get('token') ?? '';
      this.form.patchValue({
        email,
        token
      });
    });
  }

  submit(): void {
    if (this.form.invalid || this.submitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.error.set(null);
    this.success.set(null);

    const raw = this.form.getRawValue();
    const payload: ResetPasswordDto = {
      email: raw.email.trim(),
      token: raw.token.trim(),
      newPassword: raw.newPassword,
      confirmNewPassword: raw.confirmNewPassword
    };

    this.auth
      .resetPassword(payload)
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Password reset completed.');
          setTimeout(() => {
            void this.router.navigate(['/auth/login']);
          }, 1200);
        },
        error: (err: unknown) => {
          if (err instanceof Error && err.message) {
            this.error.set(err.message);
            return;
          }

          this.error.set('Unable to reset password. Please request a new reset link.');
        }
      });
  }
}
