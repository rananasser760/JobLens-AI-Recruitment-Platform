import { PaginationParams } from './api.model';

export type InterviewAgentType = 'Technical' | 'Behavioral' | 'Mixed';

export type CheatingEventType =
  | 'FaceNotVisible'
  | 'MultipleFaces'
  | 'LookingAway'
  | 'SuspiciousObject'
  | 'AudioAnomaly'
  | 'TabSwitch'
  | 'WindowBlur'
  | 'Other';

export interface InterviewSessionDto {
  id: number;
  applicationId: number;
  agentType: string;
  interviewTitle?: string | null;
  scheduledAt?: string | null;
  startedAt?: string | null;
  endedAt?: string | null;
  overallScore?: number | null;
  cheatingDetected: boolean;
  totalQuestions: number;
  answeredQuestions: number;
  status: string;
  integritySessionId?: number | null;
  interviewBackendSessionId?: string | null;
  finalReport?: string | null;
  aiFeedback?: string | null;
  candidateName: string;
  jobTitle: string;
  cheatingEventsCount: number;
}

export interface InterviewSessionListDto {
  id: number;
  interviewTitle?: string | null;
  scheduledAt?: string | null;
  status: string;
  integritySessionId?: number | null;
  interviewBackendSessionId?: string | null;
  overallScore?: number | null;
  cheatingDetected: boolean;
  candidateName: string;
  jobTitle: string;
}

export interface ScheduleInterviewDto {
  applicationId: number;
  scheduledAt: string;
  agentType?: InterviewAgentType;
  interviewTitle?: string;
  maxQuestions?: number;
  evaluationCriteria?: string;
  focusSkills?: string[];
  questionTimeLimitSeconds?: number;
  totalInterviewDurationMinutes?: number;
  proctoringStrictness?: 'Low' | 'Medium' | 'High';
}

export interface InterviewAnswerDto {
  id: number;
  questionId: number;
  answerText?: string | null;
  responseDurationSeconds?: number | null;
  aiScore?: number | null;
  aiFeedback?: string | null;
  answeredAt: string;
}

export interface InterviewQuestionDto {
  id: number;
  sessionId: number;
  questionText: string;
  orderIndex: number;
  category?: string | null;
  difficulty?: string | null;
  maxDurationSeconds?: number | null;
  isAnswered: boolean;
  answer?: InterviewAnswerDto | null;
}

export interface SubmitAnswerDto {
  questionId: number;
  answerText?: string;
  responseDurationSeconds?: number;
}

export interface ReportCheatingEventDto {
  sessionId: number;
  eventType: CheatingEventType;
  confidence?: number;
  details?: string;
  timestampSeconds?: number;
}

export interface CheatingEventDto {
  id: number;
  sessionId: number;
  eventType: string;
  confidence?: number | null;
  detectedAt: string;
  details?: string | null;
  timestampSeconds?: number | null;
}

export interface ReportBrowserEventDto {
  sessionId: number;
  tabSwitchCount?: number;
  focusLossCount?: number;
  copyPasteCount?: number;
  rightClickCount?: number;
  detailsJson?: string;
}

export interface QuestionScoreDto {
  questionText: string;
  category?: string | null;
  score?: number | null;
  feedback?: string | null;
}

export interface CheatingReportDto {
  cheatingDetected: boolean;
  totalEvents: number;
  eventsByType: Record<string, number>;
  totalTabSwitches: number;
  totalFocusLosses: number;
}

export interface InterviewReportDto {
  sessionId: number;
  candidateName: string;
  jobTitle: string;
  startedAt?: string | null;
  endedAt?: string | null;
  durationMinutes: number;
  overallScore?: number | null;
  questionScores: QuestionScoreDto[];
  cheatingReport: CheatingReportDto;
  aiFeedback?: string | null;
  recommendation?: string | null;
}

export interface InterviewRankingDto {
  applicationId: number;
  candidateId: number;
  candidateName: string;
  interviewScore: number;
  atsScore?: number | null;
  rankingScore?: number | null;
  rank: number;
  cheatingDetected: boolean;
  status: string;
}

export interface InterviewVideoUploadResponseDto {
  videoUrl?: string | null;
  storagePath?: string | null;
  uploadedAt?: string | null;
  [key: string]: unknown;
}

export interface InterviewSearchParams extends PaginationParams {
  jobId?: number;
  applicationId?: number;
  status?: string;
  fromDate?: string;
  toDate?: string;
  cheatingDetected?: boolean;
}
