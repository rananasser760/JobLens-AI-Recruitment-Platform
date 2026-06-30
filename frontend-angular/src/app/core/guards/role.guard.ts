import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { AuthSessionService } from '../auth/auth-session.service';
import { UserRole } from '../models/auth.model';

export function roleGuard(allowedRoles: UserRole[]): CanActivateFn {
  return () => {
    const router = inject(Router);
    const session = inject(AuthSessionService);

    if (session.hasAnyRole(allowedRoles)) {
      return true;
    }

    return router.parseUrl('/forbidden');
  };
}
