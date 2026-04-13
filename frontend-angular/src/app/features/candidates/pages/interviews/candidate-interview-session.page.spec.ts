import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject, Observable, of } from 'rxjs';

import {
  InterviewQuestionDto,
  InterviewSessionDto,
  ReportBrowserEventDto,
  SubmitAnswerDto
} from '../../../../core/models/interview.model';
import { InterviewsService } from '../../../interviews/interviews.service';
import { CandidateInterviewSessionPage } from './candidate-interview-session.page';

describe('CandidateInterviewSessionPage', () => {
  const paramMap$ = new BehaviorSubject(convertToParamMap({ sessionId: '7' }));

  const session: InterviewSessionDto = {
    id: 7,
    applicationId: 15,
    agentType: 'Mixed',
    interviewTitle: 'Frontend Technical Round',
    scheduledAt: '2026-04-11T10:00:00Z',
    startedAt: '2026-04-11T10:05:00Z',
    endedAt: null,
    overallScore: null,
    cheatingDetected: false,
    totalQuestions: 1,
    answeredQuestions: 0,
    status: 'Live',
    integritySessionId: null,
    interviewBackendSessionId: null,
    finalReport: null,
    aiFeedback: null,
    candidateName: 'Candidate One',
    jobTitle: 'Frontend Engineer',
    cheatingEventsCount: 0
  };

  const question: InterviewQuestionDto = {
    id: 11,
    sessionId: 7,
    questionText: 'How do you optimize Angular rendering?',
    orderIndex: 0,
    category: 'Performance',
    difficulty: 'Medium',
    maxDurationSeconds: 180,
    isAnswered: false,
    answer: null
  };

  let submitAnswerCalls: Array<{ payload: SubmitAnswerDto; audio?: File }>;
  let reportBrowserCalls: ReportBrowserEventDto[];
  let startCalls: number;

  let interviewsServiceMock: Partial<InterviewsService>;

  beforeEach(async () => {
    submitAnswerCalls = [];
    reportBrowserCalls = [];
    startCalls = 0;

    interviewsServiceMock = {
      getSession: (): Observable<any> =>
        of({ success: true, message: 'ok', data: session }),
      getQuestions: (): Observable<any> =>
        of({ success: true, message: 'ok', data: [question] }),
      start: (): Observable<any> => {
        startCalls += 1;
        return of({ success: true, message: 'ok', data: session });
      },
      end: (): Observable<any> => of({ success: true, message: 'ok', data: session }),
      submitAnswer: (payload: SubmitAnswerDto, audioFile?: File): Observable<any> => {
        submitAnswerCalls.push({ payload, audio: audioFile });
        return of({ success: true, message: 'saved', data: null });
      },
      reportBrowserEvent: (payload: ReportBrowserEventDto): Observable<any> => {
        reportBrowserCalls.push(payload);
        return of({ success: true, message: 'synced', data: null });
      }
    };

    await TestBed.configureTestingModule({
      imports: [CandidateInterviewSessionPage],
      providers: [
        { provide: InterviewsService, useValue: interviewsServiceMock as unknown as InterviewsService },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: paramMap$.asObservable()
          }
        }
      ]
    }).compileComponents();
  });

  it('loads session and questions on init', () => {
    const fixture = TestBed.createComponent(CandidateInterviewSessionPage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    expect(component.session()?.id).toBe(7);
    expect(component.questions().length).toBe(1);
    expect(component.selectedQuestion()?.id).toBe(11);
  });

  it('treats Live status as in-progress', () => {
    const fixture = TestBed.createComponent(CandidateInterviewSessionPage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    expect(component.interviewPhase()).toBe('in-progress');
  });

  it('requires microphone and camera before starting', () => {
    const fixture = TestBed.createComponent(CandidateInterviewSessionPage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    component.session.set({ ...session, status: 'Scheduled' });
    component.interviewPhase.set('pre-check');
    component.permissionsGranted.set({ mic: true, camera: false });

    component.enterInterview();

    expect(startCalls).toBe(0);
    expect(component.error()).toContain('microphone and camera');
  });

  it('submits answer draft for selected question', () => {
    const fixture = TestBed.createComponent(CandidateInterviewSessionPage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    component.onAnswerTextChanged('Use trackBy and OnPush with memoized selectors.');
    component.submitAnswer();

    expect(submitAnswerCalls.length).toBe(1);
    expect(submitAnswerCalls[0].payload.questionId).toBe(11);
    expect(submitAnswerCalls[0].payload.answerText).toContain('trackBy and OnPush');
  });

  it('reports browser events and resets counters', () => {
    const fixture = TestBed.createComponent(CandidateInterviewSessionPage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    component.browserCounters.set({
      tabSwitchCount: 1,
      focusLossCount: 2,
      copyPasteCount: 0,
      rightClickCount: 1
    });

    component.reportBrowserEvents();

    expect(reportBrowserCalls.length).toBe(1);
    expect(reportBrowserCalls[0].sessionId).toBe(7);
    expect(reportBrowserCalls[0].tabSwitchCount).toBe(1);
    expect(reportBrowserCalls[0].focusLossCount).toBe(2);
    expect(reportBrowserCalls[0].rightClickCount).toBe(1);
    expect(component.totalBrowserEvents()).toBe(0);
  });
});
