import { HttpErrorResponse } from '@angular/common/http';

export function extractApiErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    if (error instanceof Error && error.message) {
      return error.message;
    }
    return 'Unexpected error occurred.';
  }

  // Check custom ApiResponse error envelope or ProblemDetails format
  if (error.error && typeof error.error === 'object') {
    const body = error.error as {
      message?: string;
      errors?: string[] | Record<string, string[]>;
      title?: string;
      detail?: string;
    };

    if (body.message && body.message.trim().length > 0) {
      return body.message.trim();
    }

    if (Array.isArray(body.errors) && body.errors.length > 0) {
      return body.errors.join(' ');
    }

    if (body.errors && typeof body.errors === 'object') {
      const fieldErrors = Object.values(body.errors).flat();
      if (fieldErrors.length > 0) {
        return fieldErrors.join(' ');
      }
    }

    if (body.detail && body.detail.trim().length > 0) {
      return body.detail.trim();
    }

    if (body.title && body.title.trim().length > 0) {
      return body.title.trim();
    }
  }

  if (typeof error.error === 'string' && error.error.trim().length > 0) {
    try {
      const parsed = JSON.parse(error.error);
      if (parsed.message) return parsed.message;
    } catch {
      return error.error.slice(0, 160);
    }
  }

  switch (error.status) {
    case 0:
      return 'Network error: backend unreachable. Please check your connection.';
    case 400:
      return 'Request validation failed. Please review the form inputs.';
    case 401:
      return 'Session expired. Please sign in again.';
    case 403:
      return 'You do not have permission to perform this action.';
    case 404:
      return 'The requested resource was not found.';
    case 409:
      return 'A conflict occurred with the current state of the resource.';
    case 502:
    case 503:
      return 'AI microservices are temporarily unavailable. Please retry shortly.';
    default:
      return error.status >= 500
        ? 'Unexpected server error. Please try again later.'
        : `Request failed with status ${error.status}.`;
  }
}
