import { CanDeactivateFn } from '@angular/router';
import { Observable } from 'rxjs';

export interface InterviewSessionLeaveGuardAware {
  handleRouteLeaveAttempt(): boolean | Observable<boolean>;
}

export const interviewSessionLeaveGuard: CanDeactivateFn<InterviewSessionLeaveGuardAware> = (
  component
) => {
  if (!component || typeof component.handleRouteLeaveAttempt !== 'function') {
    return true;
  }

  return component.handleRouteLeaveAttempt();
};
