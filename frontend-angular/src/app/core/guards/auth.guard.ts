import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { catchError, map, of } from 'rxjs';
import { AuthSessionService } from '../auth/auth-session.service';
import { TokenStoreService } from '../auth/token-store.service';

export const authGuard: CanActivateFn = () => {
  const router = inject(Router);
  const session = inject(AuthSessionService);
  const tokens = inject(TokenStoreService);

  if (session.isAuthenticated()) {
    return true;
  }

  if (!tokens.getRefreshToken() || !tokens.getAuthUser()) {
    return router.parseUrl('/auth/login');
  }

  return session.refreshToken().pipe(
    map(() => true),
    catchError(() => of(router.parseUrl('/auth/login'))),
  );
};
