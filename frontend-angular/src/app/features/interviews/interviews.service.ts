import { Injectable } from '@angular/core';
import { Observable, timeout } from 'rxjs';
import { ApiClientService } from '../../core/api/api-client.service';
import { ApiResponse, PaginatedResponse } from '../../core/models/api.model';
import {
  CheatingEventDto,
  InterviewQuestionDto,
  InterviewRankingDto,
  InterviewReportDto,
  InterviewSearchParams,
  InterviewSessionDto,
  InterviewSessionListDto,
  InterviewVideoUploadResponseDto,
  ReportBrowserEventDto,
  ReportCheatingEventDto,
  ScheduleInterviewDto,
  SubmitAnswerDto
} from '../../core/models/interview.model';

@Injectable({ providedIn: 'root' })
export class InterviewsService {
  private readonly base = '/interviews';

  constructor(private readonly apiClient: ApiClientService) {}

  getSession(sessionId: number): Observable<ApiResponse<InterviewSessionDto>> {
    return this.apiClient.get<InterviewSessionDto>(`${this.base}/${sessionId}`);
  }

  schedule(dto: ScheduleInterviewDto): Observable<ApiResponse<InterviewSessionDto>> {
    return this.apiClient.post<InterviewSessionDto>(`${this.base}/schedule`, dto);
  }

  start(sessionId: number): Observable<ApiResponse<InterviewSessionDto>> {
    return this.apiClient
      .post<InterviewSessionDto>(`${this.base}/${sessionId}/start`, {})
      .pipe(timeout({ first: 20000 }));
  }

  end(sessionId: number): Observable<ApiResponse<InterviewSessionDto>> {
    return this.apiClient.post<InterviewSessionDto>(`${this.base}/${sessionId}/end`, {});
  }

  getQuestions(sessionId: number): Observable<ApiResponse<InterviewQuestionDto[]>> {
    return this.apiClient.get<InterviewQuestionDto[]>(`${this.base}/${sessionId}/questions`);
  }

  getNextQuestion(sessionId: number): Observable<ApiResponse<InterviewQuestionDto>> {
    return this.apiClient.get<InterviewQuestionDto>(`${this.base}/${sessionId}/next-question`);
  }

  submitAnswer(dto: SubmitAnswerDto, audioFile?: File): Observable<ApiResponse<unknown>> {
    const form = new FormData();
    form.append('questionId', String(dto.questionId));
    if (dto.answerText) {
      form.append('answerText', dto.answerText);
    }
    if (dto.responseDurationSeconds !== undefined) {
      form.append('responseDurationSeconds', String(dto.responseDurationSeconds));
    }
    if (audioFile) {
      form.append('audioFile', audioFile);
    }

    return this.apiClient.post<unknown>(`${this.base}/submit-answer`, form);
  }

  reportCheatingEvent(dto: ReportCheatingEventDto, frameImage?: File): Observable<ApiResponse<unknown>> {
    const form = new FormData();
    form.append('sessionId', String(dto.sessionId));
    form.append('eventType', dto.eventType);
    if (dto.confidence !== undefined) {
      form.append('confidence', String(dto.confidence));
    }
    if (dto.details) {
      form.append('details', dto.details);
    }
    if (dto.timestampSeconds !== undefined) {
      form.append('timestampSeconds', String(dto.timestampSeconds));
    }
    if (frameImage) {
      form.append('frameImage', frameImage);
    }

    return this.apiClient.post<unknown>(`${this.base}/cheating-event`, form);
  }

  reportBrowserEvent(dto: ReportBrowserEventDto): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${dto.sessionId}/browser-event`, dto);
  }

  cancelInterview(sessionId: number, reason?: string): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${sessionId}/cancel`, {
      reason: reason?.trim() || undefined
    });
  }

  rescheduleInterview(sessionId: number, scheduledAt: string): Observable<ApiResponse<unknown>> {
    return this.apiClient.post<unknown>(`${this.base}/${sessionId}/reschedule`, {
      scheduledAt
    });
  }

  getCheatingEvents(sessionId: number): Observable<ApiResponse<CheatingEventDto[]>> {
    return this.apiClient.get<CheatingEventDto[]>(`${this.base}/${sessionId}/cheating-events`);
  }

  getReport(sessionId: number): Observable<ApiResponse<InterviewReportDto>> {
    return this.apiClient.get<InterviewReportDto>(`${this.base}/${sessionId}/report`);
  }

  getRankings(jobId: number): Observable<ApiResponse<InterviewRankingDto[]>> {
    return this.apiClient.get<InterviewRankingDto[]>(`${this.base}/job/${jobId}/rankings`);
  }

  getRecruiterInterviews(params: InterviewSearchParams): Observable<ApiResponse<PaginatedResponse<InterviewSessionListDto>>> {
    return this.apiClient.get<PaginatedResponse<InterviewSessionListDto>>(`${this.base}/my-interviews`, params as Record<string, unknown>);
  }

  getCandidateInterviews(params: InterviewSearchParams): Observable<ApiResponse<PaginatedResponse<InterviewSessionListDto>>> {
    return this.apiClient.get<PaginatedResponse<InterviewSessionListDto>>(`${this.base}/candidate-interviews`, params as Record<string, unknown>);
  }

  uploadVideo(sessionId: number, file: File): Observable<ApiResponse<InterviewVideoUploadResponseDto>> {
    const form = new FormData();
    form.append('videoFile', file);
    return this.apiClient.post<InterviewVideoUploadResponseDto>(`${this.base}/${sessionId}/video`, form);
  }
}
