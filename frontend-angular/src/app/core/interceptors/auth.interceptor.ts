import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { TokenStoreService } from '../auth/token-store.service';

const AUTH_WHITELIST = ['/api/auth/login', '/api/auth/register', '/api/auth/forgot-password', '/api/auth/reset-password'];

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const authService = inject(AuthService);
  const tokens = inject(TokenStoreService);

  const skipAuthHeader = AUTH_WHITELIST.some((path) => req.url.includes(path));
  const isRefresh = req.url.includes('/api/auth/refresh-token');
  const accessToken = tokens.getAccessToken();

  const request = !skipAuthHeader && !!accessToken
    ? req.clone({
        setHeaders: {
          Authorization: `Bearer ${accessToken}`
        }
      })
    : req;

  return next(request).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse)) {
        return throwError(() => error);
      }

      if (error.status !== 401 || isRefresh || skipAuthHeader) {
        return throwError(() => error);
      }

      return authService.refreshToken().pipe(
        switchMap((session) => {
          const retry = req.clone({
            setHeaders: {
              Authorization: `Bearer ${session.accessToken}`
            }
          });
          return next(retry);
        }),
        catchError((refreshError) => {
          authService.hardLogout();
          return throwError(() => refreshError);
        })
      );
    })
  );
};
