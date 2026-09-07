import { CommonModule, isPlatformBrowser } from '@angular/common';
import {
  AfterViewChecked,
  Component,
  ElementRef,
  HostListener,
  OnDestroy,
  PLATFORM_ID,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subscription, catchError, finalize, forkJoin, map, of } from 'rxjs';

import { TokenStoreService } from '../../../../core/auth/token-store.service';
import { InterviewsService } from '../../../interviews/interviews.service';
import { InterviewRealtimeService, VadState } from '../../../interviews/interview-realtime.service';
import { InterviewQuestionDto, InterviewSessionDto } from '../../../../core/models/interview.model';
import { environment } from '../../../../../environments/environment';

type BrowserCounterKey = 'tabSwitchCount' | 'focusLossCount' | 'copyPasteCount' | 'rightClickCount';

interface BrowserCounters {
  tabSwitchCount: number;
  focusLossCount: number;
  copyPasteCount: number;
  rightClickCount: number;
}

type TranscriptRole = 'candidate' | 'assistant' | 'system';
type InterviewPhase =
  | 'loading'
  | 'pre-check'
  | 'in-progress'
  | 'completing'
  | 'completed'
  | 'error';

interface TranscriptEntry {
  role: TranscriptRole;
  text: string;
  createdAtIso: string;
}

interface WaveformBar {
  dur: string;
  delay: string;
  h: number;
}

const EMPTY_BROWSER_COUNTERS: BrowserCounters = {
  tabSwitchCount: 0,
  focusLossCount: 0,
  copyPasteCount: 0,
  rightClickCount: 0,
};

const WAVEFORM_COUNT = 18;

@Component({
  selector: 'app-candidate-interview-session-page',
  imports: [CommonModule, RouterLink],
  templateUrl: './candidate-interview-session.page.html',
  styleUrls: ['./candidate-interview-session.page.css'],
})
export class CandidateInterviewSessionPage implements OnDestroy, AfterViewChecked {
  private readonly route = inject(ActivatedRoute);
  private readonly interviewsService = inject(InterviewsService);
  private readonly realtime = inject(InterviewRealtimeService);
  private readonly tokenStore = inject(TokenStoreService);
  private readonly platformId = inject(PLATFORM_ID);

  @ViewChild('previewVideo') previewVideoRef!: ElementRef<HTMLVideoElement>;
  @ViewChild('arenaVideo') arenaVideoRef!: ElementRef<HTMLVideoElement>;
  @ViewChild('transcriptFeed') transcriptFeedRef!: ElementRef<HTMLDivElement>;

  private readonly isBrowser = isPlatformBrowser(this.platformId);
  private browserMonitorsAttached = false;
  private timerInterval: ReturnType<typeof setInterval> | null = null;
  private videoStreamInterval: ReturnType<typeof setInterval> | null = null;
  private shouldScrollTranscript = false;

  private subs = new Subscription();

  // ─── Phase & UI State ───
  readonly interviewPhase = signal<InterviewPhase>('loading');
  readonly loading = signal(true);
  readonly acting = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);
  readonly requestingPerms = signal(false);
  readonly permChecked = signal(false);
  readonly permissionsGranted = signal<{ camera: boolean; mic: boolean }>({
    camera: false,
    mic: false,
  });
  readonly cameraStream = signal<MediaStream | null>(null);
  readonly sessionTimerSeconds = signal(0);
  readonly confirmEndSessionOpen = signal(false);
  readonly leaveWarningOpen = signal(false);
  readonly leaveAttemptCount = signal(0);

  // ─── Session Data ───
  readonly sessionId = signal<number | null>(null);
  readonly session = signal<InterviewSessionDto | null>(null);
  readonly questions = signal<InterviewQuestionDto[]>([]);
  readonly browserCounters = signal<BrowserCounters>({ ...EMPTY_BROWSER_COUNTERS });
  readonly transcriptEntries = signal<TranscriptEntry[]>([]);
  readonly proctoringEvents = signal<string[]>([]);

  // ─── Realtime VAD Signals ───
  readonly vadState = signal<VadState>('idle');
  readonly volumeLevel = signal<number>(0);
  readonly cameraMonitoringActive = signal(false);

  readonly waveformBars: WaveformBar[] = Array.from({ length: WAVEFORM_COUNT }, (_, i) => ({
    dur: (0.5 + Math.random() * 0.8).toFixed(2),
    delay: (i * 0.05).toFixed(2),
    h: 8 + Math.floor(Math.random() * 28),
  }));

  // ─── Computed Properties ───
  readonly answeredCount = computed(() => this.questions().filter((q) => q.isAnswered).length);
  readonly totalCount = computed(() => this.questions().length || 5);
  readonly hasRequiredPermissions = computed(
    () => this.permissionsGranted().mic && this.permissionsGranted().camera,
  );
  readonly hasTranscripts = computed(() => this.transcriptEntries().length > 0);
  readonly hasProctoringEvents = computed(() => this.proctoringEvents().length > 0);
  readonly totalBrowserEvents = computed(() => {
    const c = this.browserCounters();
    return c.tabSwitchCount + c.focusLossCount + c.copyPasteCount + c.rightClickCount;
  });
  readonly hasBrowserEvents = computed(() => this.totalBrowserEvents() > 0);
  readonly formattedTimer = computed(() => {
    const s = this.sessionTimerSeconds();
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return `${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
  });

  readonly isRealtimeConnected = computed(() => this.vadState() !== 'idle');
  readonly isRealtimeConnecting = computed(() => this.vadState() === 'idle' && this.isInProgress());
  readonly liveCapturing = computed(
    () => this.vadState() === 'speaking' || this.vadState() === 'listening',
  );
  readonly audioPlaying = computed(() => this.vadState() === 'playing');
  readonly aiSpeechMode = computed<'idle' | 'audio' | 'fallback'>(() =>
    this.vadState() === 'playing' ? 'audio' : 'idle',
  );
  readonly aiConnected = computed(() => this.vadState() !== 'idle');
  readonly canUseRealtime = computed(() => this.isInProgress());

  constructor() {
    this.route.paramMap.subscribe((params) => {
      const rawId = Number(params.get('sessionId'));
      if (!Number.isFinite(rawId) || rawId <= 0) {
        this.interviewPhase.set('error');
        this.error.set('Invalid interview session identifier provided.');
        this.loading.set(false);
        return;
      }
      this.sessionId.set(rawId);
      this.load();
    });

    this.bindRealtimeEvents();
  }

  ngAfterViewChecked(): void {
    this.attachCameraToVideoElement();
    if (this.shouldScrollTranscript) {
      this.scrollTranscriptToBottom();
      this.shouldScrollTranscript = false;
    }
  }

  load(): void {
    const id = this.sessionId();
    if (!id) return;

    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      session: this.interviewsService.getSession(id).pipe(
        map((res) => res.data),
        catchError(() => of(null)),
      ),
      questions: this.interviewsService.getQuestions(id).pipe(
        map((res) => res.data ?? []),
        catchError(() => of([] as InterviewQuestionDto[])),
      ),
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ session, questions }) => {
          this.session.set(session);
          this.questions.set(questions);
          this.derivePhase(session);

          if (!session) {
            this.interviewPhase.set('error');
            this.error.set('Interview session details are unavailable.');
          }
        },
        error: () => {
          this.interviewPhase.set('error');
          this.error.set('Unable to load interview session details right now.');
        },
      });
  }

  private derivePhase(session: InterviewSessionDto | null): void {
    if (!session) {
      this.interviewPhase.set('error');
      return;
    }

    const status = (session.status ?? '').toLowerCase();
    if (status.includes('live') || status.includes('progress') || status.includes('started')) {
      this.interviewPhase.set('in-progress');
      this.startTimer();
      void this.initRealtimeFlow(session.id);
    } else if (status.includes('complet') || status.includes('ended')) {
      this.interviewPhase.set('completed');
      this.stopTimer();
    } else {
      this.interviewPhase.set('pre-check');
    }
  }
  isInProgress(): boolean {
    return this.interviewPhase() === 'in-progress' || this.interviewPhase() === 'completing';
  }

  async requestPermissions(): Promise<void> {
    if (!this.isBrowser || !navigator.mediaDevices?.getUserMedia) {
      this.error.set('Camera and microphone access are not supported in this browser.');
      return;
    }

    this.requestingPerms.set(true);
    this.error.set(null);

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: true,
        video: { width: { ideal: 640 }, height: { ideal: 480 } },
      });

      this.cameraStream.set(stream);
      this.permissionsGranted.set({ camera: true, mic: true });
      this.success.set('Microphone and camera permissions granted.');
    } catch {
      this.permissionsGranted.set({ camera: false, mic: false });
      this.error.set('Camera and microphone permissions were denied.');
    } finally {
      this.permChecked.set(true);
      this.requestingPerms.set(false);
    }
  }

  enterInterview(): void {
    const id = this.sessionId();
    if (!id || this.acting()) return;

    this.acting.set(true);
    this.error.set(null);

    this.interviewsService
      .start(id)
      .pipe(finalize(() => this.acting.set(false)))
      .subscribe({
        next: (res) => {
          if (res.data) {
            this.session.set(res.data);
          }
          this.interviewPhase.set('in-progress');
          this.startTimer();
          this.attachBrowserMonitors();
          this.initRealtimeFlow(id);
        },
        error: (err: unknown) => {
          this.error.set(
            err instanceof Error ? err.message : 'Unable to start the interview session.',
          );
        },
      });
  }

  private async initRealtimeFlow(sessionId: number): Promise<void> {
    try {
      await this.realtime.connect(sessionId);
      await this.realtime.startAudioCapture();
      this.startVideoFrameStreaming();
      await this.realtime.requestOpeningPrompt();
    } catch (err) {
      console.warn('Realtime interview connection failure:', err);
    }
  }

  private bindRealtimeEvents(): void {
    this.subs.add(this.realtime.vadState$.subscribe((state) => this.vadState.set(state)));
    this.subs.add(this.realtime.volumeLevel$.subscribe((vol) => this.volumeLevel.set(vol)));
    this.subs.add(
      this.realtime.audioProcessed$.subscribe((data) => {
        if (data.transcript) {
          this.pushTranscript('candidate', data.transcript);
        }
        if (data.reply) {
          this.pushTranscript('assistant', data.reply);
        }
        if (data.isComplete) {
          this.confirmEndSession();
        }
      }),
    );
    this.subs.add(
      this.realtime.proctoringEvent$.subscribe((event) => {
        this.proctoringEvents.update((evts) => [...evts, event.description]);
      }),
    );
  }

  connectRealtime(): void {
    const id = this.sessionId();
    if (id) void this.initRealtimeFlow(id);
  }

  endSession(): void {
    this.confirmEndSessionOpen.set(true);
  }

  confirmEndSession(): void {
    const id = this.sessionId();
    if (!id || this.acting()) return;

    this.confirmEndSessionOpen.set(false);
    this.interviewPhase.set('completing');
    this.acting.set(true);

    this.realtime.disconnect();
    this.stopVideoFrameStreaming();
    this.stopTimer();

    this.interviewsService
      .end(id)
      .pipe(finalize(() => this.acting.set(false)))
      .subscribe({
        next: (res) => {
          this.session.set(res.data);
          this.interviewPhase.set('completed');
        },
        error: (err: unknown) => {
          this.interviewPhase.set('in-progress');
          this.error.set(err instanceof Error ? err.message : 'Unable to complete interview.');
        },
      });
  }

  handleRouteLeaveAttempt(): boolean | Observable<boolean> {
    if (!this.isInProgress()) return true;

    if (this.leaveAttemptCount() === 0) {
      this.leaveAttemptCount.set(1);
      this.leaveWarningOpen.set(true);
      return false;
    }

    this.confirmEndSession();
    return true;
  }

  dismissLeaveWarning(): void {
    this.leaveWarningOpen.set(false);
  }

  endSessionAfterLeaveWarning(): void {
    this.leaveWarningOpen.set(false);
    this.confirmEndSession();
  }

  @HostListener('window:beforeunload', ['$event'])
  handleBeforeUnload(event: BeforeUnloadEvent): void {
    if (!this.isInProgress()) return;
    event.preventDefault();
    event.returnValue = '';
  }

  private startTimer(): void {
    if (this.timerInterval) return;
    this.timerInterval = setInterval(() => {
      this.sessionTimerSeconds.update((s) => s + 1);
    }, 1000);
  }

  private stopTimer(): void {
    if (this.timerInterval) {
      clearInterval(this.timerInterval);
      this.timerInterval = null;
    }
  }

  private startVideoFrameStreaming(): void {
    if (!this.isBrowser || this.videoStreamInterval) return;
    this.cameraMonitoringActive.set(true);

    const canvas = document.createElement('canvas');
    canvas.width = 320;
    canvas.height = 240;
    const ctx = canvas.getContext('2d');

    this.videoStreamInterval = setInterval(() => {
      const video = this.arenaVideoRef?.nativeElement ?? this.previewVideoRef?.nativeElement;
      if (!video || video.readyState < 2 || !ctx) return;

      ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
      const dataUrl = canvas.toDataURL('image/jpeg', 0.45);
      const base64 = dataUrl.split(',')[1];
      if (base64) {
        void this.realtime.submitVideoFrame(base64);
      }
    }, 1500);
  }

  private stopVideoFrameStreaming(): void {
    this.cameraMonitoringActive.set(false);
    if (this.videoStreamInterval) {
      clearInterval(this.videoStreamInterval);
      this.videoStreamInterval = null;
    }
  }

  private pushTranscript(role: TranscriptRole, text: string): void {
    this.transcriptEntries.update((entries) => [
      ...entries,
      { role, text, createdAtIso: new Date().toISOString() },
    ]);
    this.shouldScrollTranscript = true;
  }

  private scrollTranscriptToBottom(): void {
    const el = this.transcriptFeedRef?.nativeElement;
    if (el) el.scrollTop = el.scrollHeight;
  }

  private attachCameraToVideoElement(): void {
    const stream = this.cameraStream();
    if (!stream) return;

    if (
      this.previewVideoRef?.nativeElement &&
      this.previewVideoRef.nativeElement.srcObject !== stream
    ) {
      this.previewVideoRef.nativeElement.srcObject = stream;
    }

    if (
      this.arenaVideoRef?.nativeElement &&
      this.arenaVideoRef.nativeElement.srcObject !== stream
    ) {
      this.arenaVideoRef.nativeElement.srcObject = stream;
    }
  }

  private attachBrowserMonitors(): void {
    if (!this.isBrowser || this.browserMonitorsAttached) return;
    window.addEventListener('blur', this.handleBlur);
    document.addEventListener('visibilitychange', this.handleVisibility);
    this.browserMonitorsAttached = true;
  }

  private detachBrowserMonitors(): void {
    if (!this.isBrowser || !this.browserMonitorsAttached) return;
    window.removeEventListener('blur', this.handleBlur);
    document.removeEventListener('visibilitychange', this.handleVisibility);
    this.browserMonitorsAttached = false;
  }

  private readonly handleBlur = (): void => {
    this.bumpBrowserCounter('focusLossCount');
  };

  private readonly handleVisibility = (): void => {
    if (document.visibilityState === 'hidden') {
      this.bumpBrowserCounter('tabSwitchCount');
    }
  };

  private bumpBrowserCounter(key: BrowserCounterKey): void {
    this.browserCounters.update((c) => ({ ...c, [key]: c[key] + 1 }));
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.detachBrowserMonitors();
    this.stopTimer();
    this.stopVideoFrameStreaming();
    this.realtime.disconnect();

    const stream = this.cameraStream();
    if (stream) {
      stream.getTracks().forEach((track) => track.stop());
    }
  }
}
