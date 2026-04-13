import { HttpErrorResponse } from '@angular/common/http';

export function extractApiErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return 'Unexpected error occurred.';
  }

  const body = error.error as { message?: string; errors?: string[] } | null;
  const serverMessage = body?.message?.trim();
  if (serverMessage) {
    return serverMessage;
  }

  switch (error.status) {
    case 0:
      return 'Network error: backend unreachable.';
    case 400:
      return 'Request validation failed.';
    case 401:
      return 'Session expired. Please login again.';
    case 403:
      return 'You do not have permission for this action.';
    case 404:
      return 'Requested resource was not found.';
    case 502:
      return 'AI backend is temporarily unavailable. Please retry.';
    default:
      return 'Unexpected server error.';
  }
}
