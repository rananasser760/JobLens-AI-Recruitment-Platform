import { PaginationParams } from './api.model';

export type ApplicationStatus =
  | 'Submitted'
  | 'AtsPending'
  | 'AtsQualified'
  | 'AtsRejected'
  | 'InterviewScheduled'
  | 'InterviewCompleted'
  | 'Offered'
  | 'Rejected'
  | 'Withdrawn'
  | 'ExternalRedirected';

export interface ApplicationDto {
  id: number;
  candidateId: number;
  jobId: number;
  resumeId?: number | null;
  appliedVia: string;
  appliedAt: string;
  status: string;
  coverLetter?: string | null;
  recruiterNotes?: string | null;
  reviewedAt?: string | null;
  candidateName: string;
  candidateEmail: string;
  candidatePhone?: string | null;
  jobTitle: string;
  companyName?: string | null;
  resumeName?: string | null;
  atsScore?: number | null;
  hasInterview: boolean;
  interviewScore?: number | null;
}

export interface ApplicationListDto {
  id: number;
  jobTitle: string;
  companyName?: string | null;
  appliedAt: string;
  status: string;
  appliedVia: string;
  hasInterview: boolean;
}

export interface ApplyToJobDto {
  jobId: number;
  resumeId?: number;
  coverLetter?: string;
}

export interface UpdateApplicationStatusDto {
  status: ApplicationStatus;
  notes?: string;
}

export interface BulkUpdateApplicationStatusDto {
  applicationIds: number[];
  status: ApplicationStatus;
  notes?: string;
}

export interface BulkUpdateApplicationStatusResultDto {
  requestedCount: number;
  updatedCount: number;
  skippedCount: number;
  notFoundIds: number[];
  unauthorizedIds: number[];
}

export interface ApplicationSearchParams extends PaginationParams {
  jobId?: number;
  candidateId?: number;
  status?: ApplicationStatus;
  fromDate?: string;
  toDate?: string;
}

export interface CandidateApplicationDto {
  applicationId: number;
  candidateId: number;
  candidateName: string;
  candidateEmail: string;
  currentTitle?: string | null;
  yearsOfExperience?: number | null;
  profileImage?: string | null;
  appliedAt: string;
  status: string;
  atsScore?: number | null;
  rankingScore?: number | null;
  rank?: number | null;
  skills: string[];
  hasInterview: boolean;
  interviewScore?: number | null;
}
