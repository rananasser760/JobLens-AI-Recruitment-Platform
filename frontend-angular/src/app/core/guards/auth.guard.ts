import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { AuthSessionService } from '../auth/auth-session.service';

export const authGuard: CanActivateFn = () => {
  const router = inject(Router);
  const session = inject(AuthSessionService);

  if (session.isAuthenticated()) {
    return true;
  }

  return router.parseUrl('/auth/login');
};
