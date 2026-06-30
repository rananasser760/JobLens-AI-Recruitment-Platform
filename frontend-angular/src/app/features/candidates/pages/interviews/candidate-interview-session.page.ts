import { CommonModule, isPlatformBrowser } from '@angular/common';
import {
  AfterViewChecked,
  Component,
  computed,
  ElementRef,
  HostListener,
  inject,
  OnDestroy,
  PLATFORM_ID,
  signal,
  ViewChild
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, catchError, finalize, forkJoin, map, of } from 'rxjs';

import { TokenStoreService } from '../../../../core/auth/token-store.service';
import { InterviewsService } from '../../../interviews/interviews.service';
import {
  InterviewRealtimeService,
  RealtimeSocketLike
} from '../../../interviews/interview-realtime.service';
import { InterviewQuestionDto, InterviewSessionDto } from '../../../../core/models/interview.model';
import { environment } from '../../../../../environments/environment';

type BrowserCounterKey =
  | 'tabSwitchCount'
  | 'focusLossCount'
  | 'copyPasteCount'
  | 'rightClickCount';

interface BrowserCounters {
  tabSwitchCount: number;
  focusLossCount: number;
  copyPasteCount: number;
  rightClickCount: number;
}

type RealtimeState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting' | 'error';
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
  rightClickCount: 0
};

const WAVEFORM_COUNT = 18;

@Component({
  selector: 'app-candidate-interview-session-page',
  imports: [CommonModule, RouterLink],
  templateUrl: './candidate-interview-session.page.html',
  styleUrls: ['./candidate-interview-session.page.css']
})
export class CandidateInterviewSessionPage implements OnDestroy, AfterViewChecked {
  private readonly route = inject(ActivatedRoute);
  private readonly interviewsService = inject(InterviewsService);
  private readonly interviewRealtime = inject(InterviewRealtimeService);
  private readonly tokenStore = inject(TokenStoreService);
  private readonly platformId = inject(PLATFORM_ID);

  @ViewChild('previewVideo') previewVideoRef!: ElementRef<HTMLVideoElement>;
  @ViewChild('arenaVideo') arenaVideoRef!: ElementRef<HTMLVideoElement>;
  @ViewChild('transcriptFeed') transcriptFeedRef!: ElementRef<HTMLDivElement>;

  private readonly isBrowser = isPlatformBrowser(this.platformId);
  private browserMonitorsAttached = false;

  private realtimeSocket: RealtimeSocketLike | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectAttempts = 0;
  private lastRealtimeGatewaySessionId: string | null = null;
  private manualRealtimeDisconnect = false;

  private mediaRecorder: MediaRecorder | null = null;
  private mediaStream: MediaStream | null = null;
  private audioPlayer: HTMLAudioElement | null = null;
  private readonly audioQueue: Blob[] = [];
  private fallbackSpeechTimer: ReturnType<typeof setTimeout> | null = null;
  private frameCaptureTimer: ReturnType<typeof setInterval> | null = null;
  private frameCanvas: HTMLCanvasElement | null = null;
  private timerInterval: ReturnType<typeof setInterval> | null = null;
  private deviceHealthTimer: ReturnType<typeof setInterval> | null = null;
  private deviceGraceTimer: ReturnType<typeof setTimeout> | null = null;
  private shouldScrollTranscript = false;
  private lastAssistantReplyText: string | null = null;
  private pausedCaptureForPlayback = false;
  private providerAutoEnding = false;
  private readonly deviceGracePeriodMs = 5000;
  private captureStartedAtMs: number | null = null;
  private lastTranscriptReceivedAtMs: number | null = null;
  private noTranscriptWarningShown = false;
  private readonly noTranscriptWarningDelayMs = 12000;

  // ─── Phase & UI State ───
  readonly interviewPhase = signal<InterviewPhase>('loading');
  readonly loading = signal(true);
  readonly acting = signal(false);
  readonly answering = signal(false);
  readonly reportingBrowserEvents = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);
  readonly requestingPerms = signal(false);
  readonly permChecked = signal(false);
  readonly permissionsGranted = signal<{ camera: boolean; mic: boolean }>({
    camera: false,
    mic: false
  });
  readonly cameraStream = signal<MediaStream | null>(null);
  readonly sessionTimerSeconds = signal(0);

  // ─── Session Data ───
  readonly sessionId = signal<number | null>(null);
  readonly session = signal<InterviewSessionDto | null>(null);
  readonly questions = signal<InterviewQuestionDto[]>([]);
  readonly selectedQuestionId = signal<number | null>(null);
  readonly answerText = signal('');
  readonly selectedAudioFile = signal<File | null>(null);
  readonly questionStartedAtMs = signal<number | null>(null);
  readonly browserCounters = signal<BrowserCounters>({ ...EMPTY_BROWSER_COUNTERS });

  // ─── Realtime ───
  readonly realtimeState = signal<RealtimeState>('disconnected');
  readonly realtimeError = signal<string | null>(null);
  readonly transcriptEntries = signal<TranscriptEntry[]>([]);
  readonly liveCapturing = signal(false);
  readonly micSupported = signal(false);
  readonly audioPlaying = signal(false);
  readonly aiConnected = signal(false);
  readonly cameraMonitoringActive = signal(false);
  readonly aiSpeechMode = signal<'idle' | 'audio' | 'fallback'>('idle');
  readonly proctoringEvents = signal<string[]>([]);
  readonly confirmEndSessionOpen = signal(false);

  // ─── Waveform bars (static data) ───
  readonly waveformBars: WaveformBar[] = Array.from({ length: WAVEFORM_COUNT }, (_, i) => ({
    dur: (0.5 + Math.random() * 0.8).toFixed(2),
    delay: (i * 0.05).toFixed(2),
    h: 8 + Math.floor(Math.random() * 28)
  }));

  // ─── Computed ───
  readonly answeredCount = computed(() => this.questions().filter((q) => q.isAnswered).length);
  readonly totalCount = computed(
    () => this.questions().length || this.session()?.totalQuestions || 0
  );
  readonly progressPercent = computed(() => {
    const total = this.totalCount();
    return total <= 0 ? 0 : Math.min(100, Math.round((this.answeredCount() / total) * 100));
  });
  readonly nextQuestion = computed(
    () => this.questions().find((q) => !q.isAnswered) ?? null
  );
  readonly selectedQuestion = computed(
    () => this.questions().find((q) => q.id === this.selectedQuestionId()) ?? null
  );
  readonly responseDurationSeconds = computed(() => {
    const startedAt = this.questionStartedAtMs();
    return !startedAt ? 0 : Math.max(1, Math.round((Date.now() - startedAt) / 1000));
  });
  readonly hasBrowserEvents = computed(() => {
    const c = this.browserCounters();
    return c.tabSwitchCount + c.focusLossCount + c.copyPasteCount + c.rightClickCount > 0;
  });
  readonly totalBrowserEvents = computed(() => {
    const c = this.browserCounters();
    return c.tabSwitchCount + c.focusLossCount + c.copyPasteCount + c.rightClickCount;
  });
  readonly canSubmitAnswer = computed(() => {
    const q = this.selectedQuestion();
    return !!q && !q.isAnswered && (!!this.answerText().trim() || !!this.selectedAudioFile());
  });
  readonly hasRealtimeSession = computed(() => !!this.session()?.interviewBackendSessionId);
  readonly canUseRealtime = computed(() => this.isInProgress() && this.hasRealtimeSession());
  readonly isRealtimeConnected = computed(() => this.realtimeState() === 'connected');
  readonly isRealtimeConnecting = computed(() => {
    const s = this.realtimeState();
    return s === 'connecting' || s === 'reconnecting';
  });
  readonly hasRequiredPermissions = computed(
    () => this.permissionsGranted().mic && this.permissionsGranted().camera
  );
  readonly hasTranscripts = computed(() => this.transcriptEntries().length > 0);
  readonly hasProctoringEvents = computed(() => this.proctoringEvents().length > 0);
  readonly formattedTimer = computed(() => {
    const s = this.sessionTimerSeconds();
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return `${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
  });
  readonly leaveWarningOpen = signal(false);
  readonly leaveAttemptCount = signal(0);

  constructor() {
    if (this.isBrowser) {
      this.micSupported.set(typeof MediaRecorder !== 'undefined');
    }

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
  }

  ngAfterViewChecked(): void {
    // Wire camera streams to video elements as soon as they're in the DOM
    this.attachCameraToVideoElement();

    // Scroll transcript to bottom when new entries arrive
    if (this.shouldScrollTranscript) {
      this.scrollTranscriptToBottom();
      this.shouldScrollTranscript = false;
    }
  }

  // ─── Load ───
  load(): void {
    const id = this.sessionId();
    if (!id) return;

    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      session: this.interviewsService.getSession(id).pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      questions: this.interviewsService.getQuestions(id).pipe(
        map((res) => res.data ?? []),
        catchError(() => of([] as InterviewQuestionDto[]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ session, questions }) => {
          this.session.set(session);
          this.questions.set(questions);
          this.pickQuestion(questions);
          this.derivePhase(session);
          this.syncBrowserMonitorState();

          if (!session) {
            this.interviewPhase.set('error');
            this.error.set('Interview session details are unavailable.');
          }
        },
        error: () => {
          this.disconnectRealtime(true);
          this.stopLiveCapture();
          this.interviewPhase.set('error');
          this.error.set('Unable to load interview session details right now.');
        }
      });
  }

  // ─── Phase derivation ───
  private derivePhase(session: InterviewSessionDto | null): void {
    if (!session) {
      this.stopDeviceHealthMonitor();
      this.interviewPhase.set('error');
      return;
    }
    const status = this.normalizeStatus(session.status);
    if (this.isLiveStatus(status)) {
      this.interviewPhase.set('in-progress');
      this.startTimer();
      this.startDeviceHealthMonitor();
      this.syncRealtimeState();
      if (!this.hasRequiredPermissions() && !this.requestingPerms()) {
        void this.requestPermissions(true);
      }
    } else if (this.isCompletedStatus(status)) {
      this.interviewPhase.set('completed');
      this.stopTimer();
      this.stopDeviceHealthMonitor();
    } else if (this.isCancelledStatus(status)) {
      this.interviewPhase.set('error');
      this.error.set('This interview session has been cancelled.');
      this.stopDeviceHealthMonitor();
    } else {
      // Anything else (Scheduled, Draft, Pending, Created, NotStarted, etc.) → pre-check
      this.interviewPhase.set('pre-check');
      this.stopDeviceHealthMonitor();
    }
  }

  isInProgress(): boolean {
    const p = this.interviewPhase();
    return p === 'in-progress' || p === 'completing';
  }

  canStart(): boolean {
    const status = this.normalizeStatus(this.session()?.status);
    // Allow starting unless the session is already in-progress, completed, or cancelled
    if (!status) return false;
    return (
      !this.isLiveStatus(status) &&
      !this.isCompletedStatus(status) &&
      !this.isCancelledStatus(status)
    );
  }

  canEnd(): boolean {
    const status = this.normalizeStatus(this.session()?.status);
    return this.isLiveStatus(status);
  }

  // ─── Permissions ───
  async requestPermissions(silent = false): Promise<void> {
    if (!this.isBrowser || !navigator.mediaDevices?.getUserMedia) {
      this.error.set('This browser does not support camera and microphone access.');
      return;
    }

    this.requestingPerms.set(true);
    this.permChecked.set(false);
    this.error.set(null);
    this.success.set(null);

    try {
      try {
        const stream = await navigator.mediaDevices.getUserMedia({
          audio: this.getAudioConstraints(),
          video: true
        });
        this.releaseCameraStream();
        this.cameraStream.set(stream);
        this.permissionsGranted.set({ camera: true, mic: true });
        if (!silent) {
          this.success.set('Microphone and camera are ready. You can start the interview.');
        }
        this.clearDeviceGraceTimer();
        return;
      } catch {
        // Fall through and detect partial permission grants.
      }

      let micGranted = false;
      let cameraGranted = false;
      let videoOnlyStream: MediaStream | null = null;

      try {
        const micOnlyStream = await navigator.mediaDevices.getUserMedia({
          audio: this.getAudioConstraints(),
          video: false
        });
        micGranted = true;
        for (const track of micOnlyStream.getTracks()) track.stop();
      } catch {}

      try {
        videoOnlyStream = await navigator.mediaDevices.getUserMedia({
          audio: false,
          video: true
        });
        cameraGranted = true;
      } catch {}

      this.releaseCameraStream();
      if (videoOnlyStream) {
        this.cameraStream.set(videoOnlyStream);
      }

      this.permissionsGranted.set({ camera: cameraGranted, mic: micGranted });
      if (!cameraGranted || !micGranted) {
        this.error.set(
          'Microphone and camera access are required before starting the interview. Please allow both permissions and try again.'
        );
      } else {
        this.clearDeviceGraceTimer();
      }
    } finally {
      this.permChecked.set(true);
      this.requestingPerms.set(false);
    }
  }

  private getAudioConstraints(): MediaTrackConstraints {
    return {
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
      channelCount: 1,
      sampleRate: { ideal: 16000 }
    };
  }

  private attachCameraToVideoElement(): void {
    const stream = this.cameraStream();
    if (!stream) return;

    const targets: (ElementRef<HTMLVideoElement> | undefined)[] = [
      this.previewVideoRef,
      this.arenaVideoRef
    ];
    for (const ref of targets) {
      const element = ref?.nativeElement;
      if (!element) {
        continue;
      }

      if (element.srcObject !== stream) {
        element.srcObject = stream;
      }

      // Force local camera feeds to stay silent so only AI playback is audible.
      if (!element.muted) {
        element.muted = true;
      }
      if (!element.defaultMuted) {
        element.defaultMuted = true;
      }
      if (element.volume !== 0) {
        element.volume = 0;
      }
      if (!element.hasAttribute('muted')) {
        element.setAttribute('muted', '');
      }
    }
  }

  // ─── Enter Interview (Pre-check → In-progress) ───
  enterInterview(): void {
    if (this.acting()) {
      return;
    }

    if (!this.hasRequiredPermissions()) {
      this.error.set('Please grant microphone and camera access before starting the interview.');
      return;
    }

    if (!this.canStart()) {
      const status = this.session()?.status ?? 'unknown';
      this.error.set(
        `Cannot start the interview right now. Current session status: "${status}". ` +
        `Please refresh or contact support if this persists.`
      );
      return;
    }

    this.startSessionInternal();
  }

  private startSessionInternal(): void {
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
          this.leaveAttemptCount.set(0);
          this.leaveWarningOpen.set(false);
          this.providerAutoEnding = false;
          this.interviewPhase.set('in-progress');
          this.syncBrowserMonitorState();
          this.startTimer();
          this.startDeviceHealthMonitor();
          this.aiConnected.set(false);
          this.syncRealtimeState();
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to start the interview session.'));
        }
      });
  }

  // ─── End Session ───
  endSession(): void {
    const id = this.sessionId();
    if (!id || this.acting() || !this.canEnd()) return;
    this.confirmEndSessionOpen.set(true);
  }

  cancelEndSession(): void {
    if (this.acting()) return;
    this.confirmEndSessionOpen.set(false);
  }

  confirmEndSession(): void {
    const id = this.sessionId();
    if (!id || this.acting() || !this.canEnd()) {
      this.cancelEndSession();
      return;
    }

    this.confirmEndSessionOpen.set(false);
    this.interviewPhase.set('completing');
    this.acting.set(true);
    this.error.set(null);

    if (this.hasBrowserEvents()) {
      this.reportBrowserEvents(true);
    }

    this.interviewsService
      .end(id)
      .pipe(finalize(() => this.acting.set(false)))
      .subscribe({
        next: (res) => {
          this.finalizeSessionCompletion(res.data);
        },
        error: (err: unknown) => {
          this.interviewPhase.set('in-progress');
          this.error.set(this.mapError(err, 'Unable to end the interview session right now.'));
        }
      });
  }

  handleRouteLeaveAttempt(): boolean | Observable<boolean> {
    if (!this.isInProgress() || !this.canEnd()) {
      return true;
    }

    if (this.leaveAttemptCount() === 0) {
      this.leaveAttemptCount.set(1);
      this.leaveWarningOpen.set(true);
      this.error.set('Leaving this live interview is blocked. A second leave attempt will end your interview automatically.');
      return false;
    }

    this.leaveWarningOpen.set(false);
    return this.forceAutoEndSession('second_route_leave_attempt');
  }

  dismissLeaveWarning(): void {
    this.leaveWarningOpen.set(false);
  }

  endSessionAfterLeaveWarning(): void {
    this.leaveWarningOpen.set(false);
    this.leaveAttemptCount.set(2);
    this.forceAutoEndSession('leave_warning_confirm_end').subscribe(() => {});
  }

  @HostListener('window:beforeunload', ['$event'])
  handleBeforeUnload(event: BeforeUnloadEvent): void {
    if (!this.isInProgress() || !this.canEnd()) {
      return;
    }

    this.tryEndWithKeepAlive('browser_unload');
    event.preventDefault();
    event.returnValue = '';
  }

  private forceAutoEndSession(reason: string): Observable<boolean> {
    const id = this.sessionId();
    if (!id || this.acting() || !this.canEnd()) {
      return of(false);
    }

    this.interviewPhase.set('completing');
    this.acting.set(true);
    this.error.set(`Interview is ending automatically (${reason.replaceAll('_', ' ')}).`);

    if (this.hasBrowserEvents()) {
      this.reportBrowserEvents(true);
    }

    return this.interviewsService.end(id).pipe(
      map((res) => {
        this.finalizeSessionCompletion(res.data);
        return true;
      }),
      catchError((err: unknown) => {
        this.interviewPhase.set('in-progress');
        this.error.set(this.mapError(err, 'Unable to end the interview session automatically.'));
        return of(false);
      }),
      finalize(() => this.acting.set(false))
    );
  }

  private finalizeSessionCompletion(session: InterviewSessionDto | null): void {
    this.stopLiveCapture();
    this.disconnectRealtime(true);
    this.stopTimer();
    this.stopDeviceHealthMonitor();
    this.releaseCameraStream();
    this.session.set(session);
    this.leaveWarningOpen.set(false);
    this.leaveAttemptCount.set(0);
    this.providerAutoEnding = false;
    this.interviewPhase.set('completed');
  }

  private tryEndWithKeepAlive(reason: string): void {
    if (!this.isBrowser) {
      return;
    }

    const id = this.sessionId();
    const token = this.tokenStore.getAccessToken();
    if (!id || !token) {
      return;
    }

    const url = `${environment.apiBaseUrl}${environment.apiPrefix}/interviews/${id}/end`;
    void fetch(url, {
      method: 'POST',
      keepalive: true,
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`
      },
      body: JSON.stringify({ reason })
    }).catch(() => {});
  }

  // ─── Timer ───
  private startTimer(): void {
    if (this.timerInterval) return;
    this.sessionTimerSeconds.set(0);
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

  // ─── Realtime ───
  connectRealtime(): void {
    this.manualRealtimeDisconnect = false;
    if (!this.canUseRealtime()) {
      this.realtimeError.set('Live mode is only available after the interview starts.');
      return;
    }
    this.ensureRealtimeConnection(false);
  }

  disconnectRealtimeManually(): void {
    this.stopLiveCapture();
    this.disconnectRealtime(true);
    this.realtimeState.set('disconnected');
  }

  toggleLiveCapture(): void {
    if (!this.isInProgress()) {
      this.error.set('Microphone controls are only available during a live interview session.');
      return;
    }

    if (this.liveCapturing()) {
      this.stopLiveCapture();
      return;
    }
    void this.startLiveCapture();
  }

  realtimeStateLabel(): string {
    switch (this.realtimeState()) {
      case 'connected':
        return 'Connected';
      case 'connecting':
        return 'Connecting';
      case 'reconnecting':
        return 'Reconnecting';
      case 'error':
        return 'Connection issue';
      default:
        return 'Disconnected';
    }
  }

  private async startLiveCapture(): Promise<void> {
    if (!this.isBrowser || !this.micSupported()) {
      this.realtimeError.set('Microphone streaming is not supported in this browser.');
      return;
    }
    if (!this.isRealtimeConnected()) {
      this.realtimeError.set(
        `Live connection is not ready (${this.realtimeStateLabel().toLowerCase()}). Wait until it becomes connected, then retry microphone capture.`
      );
      return;
    }
    if (this.liveCapturing()) return;

    try {
      // Always build an audio-only stream for the MediaRecorder.
      // Using the full camera stream (audio+video) causes some browsers to
      // produce empty or corrupted audio chunks when an audio-only MIME type
      // is specified.
      let audioStream: MediaStream | null = null;

      const camStream = this.cameraStream();
      if (camStream && this.hasLiveEnabledTrack(camStream, 'audio')) {
        // Clone only the audio tracks into a dedicated audio-only stream.
        audioStream = new MediaStream(camStream.getAudioTracks());
      }

      if (!audioStream || audioStream.getAudioTracks().length === 0) {
        // No usable audio track on the camera stream — request a fresh one.
        audioStream = await navigator.mediaDevices.getUserMedia({
          audio: this.getAudioConstraints(),
          video: false
        });
      }

      const preferredMimeTypes = [
        'audio/webm;codecs=opus',
        'audio/webm',
        'audio/ogg;codecs=opus'
      ];
      const selectedMimeType = preferredMimeTypes.find((t) => MediaRecorder.isTypeSupported(t));
      const recorder = selectedMimeType
        ? new MediaRecorder(audioStream, { mimeType: selectedMimeType })
        : new MediaRecorder(audioStream);

      recorder.ondataavailable = (event: BlobEvent) => {
        const socket = this.realtimeSocket;
        if (!socket || socket.readyState !== WebSocket.OPEN || !event.data || event.data.size === 0) {
          return;
        }

        void event.data
          .arrayBuffer()
          .then((buffer) => {
            if (this.realtimeSocket && this.realtimeSocket.readyState === WebSocket.OPEN) {
              this.realtimeSocket.send(buffer);
              this.maybeWarnMissingTranscript();
            }
          })
          .catch(() => {});
      };

      recorder.start(1500);
      this.mediaRecorder = recorder;
      this.mediaStream = audioStream;
      this.pausedCaptureForPlayback = false;
      this.captureStartedAtMs = Date.now();
      this.lastTranscriptReceivedAtMs = null;
      this.noTranscriptWarningShown = false;
      this.liveCapturing.set(true);
      this.realtimeError.set(null);
      this.pushTranscript('system', 'Live microphone streaming started.');
    } catch (err: unknown) {
      this.realtimeError.set(this.mapError(err, 'Unable to access your microphone.'));
      this.captureStartedAtMs = null;
      this.lastTranscriptReceivedAtMs = null;
      this.noTranscriptWarningShown = false;
      this.releaseMediaResources();
    }
  }

  private stopLiveCapture(): void {
    this.stopLiveCaptureInternal(true);
  }

  private stopLiveCaptureInternal(announce: boolean): void {
    if (!this.liveCapturing()) {
      this.releaseMediaResources();
      if (announce) {
        this.pausedCaptureForPlayback = false;
      }
      return;
    }

    if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive') {
      this.mediaRecorder.stop();
    }

    this.releaseMediaResources();
    this.captureStartedAtMs = null;
    this.lastTranscriptReceivedAtMs = null;
    this.noTranscriptWarningShown = false;
    if (announce) {
      this.pausedCaptureForPlayback = false;
      this.pushTranscript('system', 'Live microphone streaming stopped.');
    }
  }

  private pauseCaptureForAiPlayback(): void {
    // Always mark that we intend to pause for playback, even if capture
    // hasn't started yet (race condition with opening prompt).
    this.pausedCaptureForPlayback = true;

    if (!this.liveCapturing()) {
      return;
    }

    // Pause the existing recorder instead of destroying it.
    if (this.mediaRecorder && this.mediaRecorder.state === 'recording') {
      this.mediaRecorder.pause();
    }
  }

  private resumeCaptureAfterAiPlayback(): void {
    if (!this.pausedCaptureForPlayback) {
      return;
    }

    this.pausedCaptureForPlayback = false;
    if (!this.canUseRealtime() || !this.isRealtimeConnected()) {
      return;
    }

    // Resume the paused recorder if it's still alive.
    if (this.mediaRecorder && this.mediaRecorder.state === 'paused') {
      this.mediaRecorder.resume();
      return;
    }

    // Recorder was lost or capture never started — start fresh.
    void this.startLiveCapture();
  }

  private syncRealtimeState(): void {
    if (!this.isBrowser) return;
    const gatewaySessionId = this.session()?.interviewBackendSessionId;
    if (!gatewaySessionId || !this.canUseRealtime()) {
      this.stopLiveCapture();
      this.disconnectRealtime(false);
      this.realtimeState.set('disconnected');
      return;
    }
    const nextKey = String(gatewaySessionId);
    if (nextKey !== this.lastRealtimeGatewaySessionId) {
      this.lastRealtimeGatewaySessionId = nextKey;
      this.transcriptEntries.set([]);
      this.proctoringEvents.set([]);
      this.disconnectRealtime(false);
    }
    if (this.manualRealtimeDisconnect) return;
    this.ensureRealtimeConnection(false);
  }

  private ensureRealtimeConnection(isReconnect: boolean): void {
    if (!this.isBrowser || !this.canUseRealtime()) return;
    const sessionId = this.sessionId();
    if (!sessionId) return;
    if (
      this.realtimeSocket &&
      (this.realtimeSocket.readyState === WebSocket.OPEN ||
        this.realtimeSocket.readyState === WebSocket.CONNECTING)
    )
      return;

    this.realtimeState.set(isReconnect ? 'reconnecting' : 'connecting');

    let socket: RealtimeSocketLike;
    try {
      socket = this.interviewRealtime.connectInterview(sessionId);
    } catch (err: unknown) {
      this.realtimeState.set('error');
      this.realtimeError.set(this.mapError(err, 'Unable to initialize live connection.'));
      return;
    }

    this.realtimeSocket = socket;

    socket.onopen = () => {
      this.reconnectAttempts = 0;
      this.realtimeState.set('connected');
      this.realtimeError.set(null);
      this.aiConnected.set(true);
      this.startVideoFrameStreaming();
      socket.requestOpeningPrompt();
      void this.startLiveCapture();
    };

    socket.onmessage = (event: MessageEvent) => {
      this.handleRealtimePayload(event.data);
    };

    socket.onerror = () => {
      this.realtimeError.set('Live connection encountered an error.');
    };

    socket.onclose = () => {
      this.realtimeSocket = null;
      this.aiConnected.set(false);
      this.stopVideoFrameStreaming();
      if (this.liveCapturing()) this.stopLiveCapture();
      const shouldReconnect = !this.manualRealtimeDisconnect && this.canUseRealtime();
      if (shouldReconnect) {
        this.scheduleReconnect();
        return;
      }
      this.realtimeState.set('disconnected');
    };
  }

  private disconnectRealtime(manual: boolean): void {
    this.manualRealtimeDisconnect = manual;
    this.aiConnected.set(false);
    this.clearReconnectTimer();
    this.stopVideoFrameStreaming();
    if (this.realtimeSocket) {
      this.interviewRealtime.close(this.realtimeSocket);
      this.realtimeSocket = null;
    }
    if (manual) this.reconnectAttempts = 0;
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= 5) {
      this.realtimeState.set('error');
      this.realtimeError.set('Live connection could not be restored. Please check your network.');
      return;
    }
    this.reconnectAttempts += 1;
    this.realtimeState.set('reconnecting');
    const waitMs = Math.min(8000, 1000 * this.reconnectAttempts);
    this.clearReconnectTimer();
    this.reconnectTimer = setTimeout(() => this.ensureRealtimeConnection(true), waitMs);
  }

  private clearReconnectTimer(): void {
    if (!this.reconnectTimer) return;
    clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
  }

  private handleRealtimePayload(payload: unknown): void {
    if (typeof payload === 'string') {
      this.handleRealtimeTextPayload(payload);
      return;
    }
    if (payload instanceof Blob) {
      this.clearFallbackSpeechTimer();
      this.cancelBrowserSpeech();
      this.enqueueAudio(payload);
      return;
    }
    if (payload instanceof ArrayBuffer) {
      this.clearFallbackSpeechTimer();
      this.cancelBrowserSpeech();
      this.enqueueAudio(new Blob([payload]));
    }
  }

  private handleRealtimeTextPayload(raw: string): void {
    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(raw) as Record<string, unknown>;
    } catch {
      return;
    }

    if (parsed['ping']) return;
    if (parsed['type'] === 'error') {
      const message = typeof parsed['message'] === 'string' ? parsed['message'].trim() : '';
      const code = typeof parsed['code'] === 'string' ? parsed['code'].trim() : '';

      if (message) {
        this.pushTranscript('system', message);
        this.error.set(message);
      }

      if (code.startsWith('Provider') && this.canEnd() && !this.providerAutoEnding) {
        this.providerAutoEnding = true;
        this.forceAutoEndSession(`provider_failure_${code.toLowerCase()}`).subscribe(() => {
          this.providerAutoEnding = false;
        });
      }
      return;
    }

    if (parsed['type'] === 'proctoring') {
      const alerts = Array.isArray(parsed['events'])
        ? parsed['events'].filter((entry): entry is string => typeof entry === 'string' && !!entry.trim())
        : [];
      
      if (alerts.length > 0) {
        const prevAlerts = this.proctoringEvents();
        const newAlerts = alerts.filter(a => !prevAlerts.includes(a));

        if (newAlerts.length > 0) {
          this.proctoringEvents.update(prev => Array.from(new Set([...prev, ...alerts])));
          this.pushTranscript('system', `Proctoring Alert: ${newAlerts.join(', ')}`);
        }
      }
      return;
    }
    if (parsed['type'] !== 'transcript') return;

    const userText = typeof parsed['user'] === 'string' ? parsed['user'].trim() : '';
    const aiText = typeof parsed['ai'] === 'string' ? parsed['ai'].trim() : '';
    const isComplete = !!parsed['is_complete'];
    const hasAudio = parsed['has_audio'] === true;

    if (userText || aiText) {
      this.lastTranscriptReceivedAtMs = Date.now();
      this.noTranscriptWarningShown = false;
    }

    if (userText && userText !== '(Silence / Inaudible)' && userText !== '(System)') {
      this.pushTranscript('candidate', userText);
    }
    if (userText === '(System)' && aiText) {
      this.pushTranscript('system', aiText);
    } else if (aiText) {
      this.lastAssistantReplyText = aiText;
      this.pushTranscript('assistant', aiText);
      if (hasAudio) {
        this.aiSpeechMode.set('audio');
        this.clearFallbackSpeechTimer();
      } else {
        this.scheduleFallbackSpeech(aiText);
      }
    }

    if (isComplete) {
      this.stopLiveCapture();
      if (this.canEnd()) {
        this.pushTranscript('system', 'AI interview completed. Generating your report...');
        this.forceAutoEndSession('ai_interview_complete').subscribe();
      }
    }
  }

  private pushTranscript(role: TranscriptRole, text: string): void {
    const trimmed = text.trim();
    if (!trimmed) return;
    this.transcriptEntries.update((entries) => [
      ...entries,
      { role, text: trimmed, createdAtIso: new Date().toISOString() }
    ]);
    this.shouldScrollTranscript = true;
  }

  private scrollTranscriptToBottom(): void {
    const el = this.transcriptFeedRef?.nativeElement;
    if (el) el.scrollTop = el.scrollHeight;
  }

  private enqueueAudio(audioBlob: Blob): void {
    this.cancelBrowserSpeech();
    this.audioQueue.push(audioBlob);
    if (this.audioPlaying()) return;
    this.playNextAudio();
  }

  private scheduleFallbackSpeech(text: string): void {
    if (!this.isBrowser) return;

    const content = text.trim();
    if (!content) return;

    const canSpeak = this.canUseBrowserSpeechSynthesis();
    if (this.isInProgress() && canSpeak) {
      this.pauseCaptureForAiPlayback();
    }

    this.clearFallbackSpeechTimer();
    this.fallbackSpeechTimer = setTimeout(() => {
      this.aiSpeechMode.set('fallback');
      if (!this.speakWithBrowserVoice(content)) {
        this.aiSpeechMode.set('idle');
        this.resumeCaptureAfterAiPlayback();
      }
    }, 300);
  }

  private clearFallbackSpeechTimer(): void {
    if (!this.fallbackSpeechTimer) return;
    clearTimeout(this.fallbackSpeechTimer);
    this.fallbackSpeechTimer = null;
  }

  private speakWithBrowserVoice(text: string): boolean {
    if (!this.canUseBrowserSpeechSynthesis()) {
      return false;
    }

    const synth = (window as Window & { speechSynthesis?: SpeechSynthesis }).speechSynthesis!;
    synth.cancel();

    const utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = 'en-US';
    utterance.rate = 1;
    utterance.pitch = 1;
    utterance.onend = () => {
      if (!this.audioPlaying()) {
        this.aiSpeechMode.set('idle');
      }
      this.resumeCaptureAfterAiPlayback();
    };
    utterance.onerror = () => {
      this.aiSpeechMode.set('idle');
      this.resumeCaptureAfterAiPlayback();
    };
    synth.speak(utterance);
    return true;
  }

  private cancelBrowserSpeech(): void {
    if (!this.canUseBrowserSpeechSynthesis()) {
      return;
    }

    (window as Window & { speechSynthesis?: SpeechSynthesis }).speechSynthesis?.cancel();
  }

  private playNextAudio(): void {
    const next = this.audioQueue.shift();
    if (!next) {
      this.audioPlaying.set(false);
      this.resumeCaptureAfterAiPlayback();
      return;
    }

    this.pauseCaptureForAiPlayback();

    const url = URL.createObjectURL(next);
    const player = new Audio(url);
    this.audioPlayer = player;
    this.audioPlaying.set(true);
    this.aiSpeechMode.set('audio');

    player.onended = () => {
      URL.revokeObjectURL(url);
      this.playNextAudio();
    };
    player.onerror = () => {
      URL.revokeObjectURL(url);
      this.playNextAudio();
    };
    void player.play().catch(() => {
      URL.revokeObjectURL(url);
      const fallbackText = this.lastAssistantReplyText;
      if (fallbackText) {
        this.aiSpeechMode.set('fallback');
        if (!this.speakWithBrowserVoice(fallbackText)) {
          this.resumeCaptureAfterAiPlayback();
        }
      }
      this.playNextAudio();
    });
  }

  private releaseMediaResources(): void {
    if (this.mediaRecorder) {
      this.mediaRecorder.ondataavailable = null;
      this.mediaRecorder = null;
    }
    if (this.mediaStream) {
      // Only stop tracks if this stream is NOT the same as the camera stream
      const camStream = this.cameraStream();
      if (this.mediaStream !== camStream) {
        for (const track of this.mediaStream.getTracks()) track.stop();
      }
      this.mediaStream = null;
    }
    this.liveCapturing.set(false);
  }

  private releaseCameraStream(): void {
    const stream = this.cameraStream();
    if (stream) {
      for (const track of stream.getTracks()) track.stop();
      this.cameraStream.set(null);
    }
  }

  // ─── Browser Event Reporting ───
  reportBrowserEvents(silent = false): void {
    const id = this.sessionId();
    if (!id || this.reportingBrowserEvents() || !this.hasBrowserEvents()) return;

    this.reportingBrowserEvents.set(true);
    const counters = this.browserCounters();

    this.interviewsService
      .reportBrowserEvent({
        sessionId: id,
        tabSwitchCount: counters.tabSwitchCount,
        focusLossCount: counters.focusLossCount,
        copyPasteCount: counters.copyPasteCount,
        rightClickCount: counters.rightClickCount,
        detailsJson: JSON.stringify({
          capturedAt: new Date().toISOString(),
          sessionStatus: this.session()?.status ?? null,
          totalSignals: this.totalBrowserEvents()
        })
      })
      .pipe(finalize(() => this.reportingBrowserEvents.set(false)))
      .subscribe({
        next: () => {
          this.browserCounters.set({ ...EMPTY_BROWSER_COUNTERS });
        },
        error: () => {}
      });
  }

  // ─── Question Management ───
  selectQuestion(questionId: number): void {
    const question = this.questions().find((q) => q.id === questionId) ?? null;
    if (!question) return;
    this.selectedQuestionId.set(question.id);
    this.resetAnswerDraft(question);
  }

  onAnswerTextChanged(value: string): void {
    this.answerText.set(value);
  }

  onAudioFileSelected(fileList: FileList | null): void {
    this.selectedAudioFile.set(fileList?.item(0) ?? null);
  }

  clearAnswerDraft(): void {
    this.answerText.set('');
    this.selectedAudioFile.set(null);
    if (this.selectedQuestion() && !this.selectedQuestion()?.isAnswered) {
      this.questionStartedAtMs.set(Date.now());
    }
  }

  submitAnswer(): void {
    const question = this.selectedQuestion();
    if (!question || question.isAnswered || this.answering()) return;

    const answerText = this.answerText().trim();
    const audioFile = this.selectedAudioFile() ?? undefined;
    if (!answerText && !audioFile) {
      this.error.set('Provide either text or audio before submitting your answer.');
      return;
    }

    this.answering.set(true);
    this.error.set(null);

    this.interviewsService
      .submitAnswer(
        {
          questionId: question.id,
          answerText: answerText || undefined,
          responseDurationSeconds: this.responseDurationSeconds() || undefined
        },
        audioFile
      )
      .pipe(finalize(() => this.answering.set(false)))
      .subscribe({
        next: () => this.load(),
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to submit your answer right now.'));
        }
      });
  }

  formatStatus(status: string | null | undefined): string {
    if (!status) return 'Unknown';
    return status.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ').trim();
  }

  private normalizeStatus(status: string | null | undefined): string {
    return (status ?? '').trim().toLowerCase();
  }

  private isLiveStatus(status: string): boolean {
    return (
      status.includes('live') ||
      status.includes('progress') ||
      status.includes('started') ||
      status.includes('inprogress')
    );
  }

  private isCompletedStatus(status: string): boolean {
    return status.includes('complet') || status.includes('ended') || status.includes('graded');
  }

  private isCancelledStatus(status: string): boolean {
    return status.includes('cancel');
  }

  // ─── Lifecycle ───
  ngOnDestroy(): void {
    this.detachBrowserMonitors();
    this.stopVideoFrameStreaming();
    this.stopDeviceHealthMonitor();
    this.stopLiveCapture();
    this.disconnectRealtime(true);
    this.clearReconnectTimer();
    this.stopTimer();
    this.releaseCameraStream();
    if (this.audioPlayer) {
      this.audioPlayer.pause();
      this.audioPlayer = null;
    }
    this.audioQueue.length = 0;
    this.clearFallbackSpeechTimer();
    this.cancelBrowserSpeech();
    if (this.hasBrowserEvents()) this.reportBrowserEvents(true);
  }

  // ─── Private helpers ───
  private pickQuestion(questions: InterviewQuestionDto[]): void {
    if (questions.length === 0) {
      this.selectedQuestionId.set(null);
      this.answerText.set('');
      this.selectedAudioFile.set(null);
      this.questionStartedAtMs.set(null);
      return;
    }
    const currentId = this.selectedQuestionId();
    const current =
      currentId !== null ? questions.find((q) => q.id === currentId) ?? null : null;
    const target = current ?? questions.find((q) => !q.isAnswered) ?? questions[0];
    this.selectedQuestionId.set(target.id);
    this.resetAnswerDraft(target);
  }

  private resetAnswerDraft(question: InterviewQuestionDto): void {
    this.answerText.set('');
    this.selectedAudioFile.set(null);
    this.questionStartedAtMs.set(question.isAnswered ? null : Date.now());
  }

  private syncBrowserMonitorState(): void {
    if (!this.isBrowser) return;
    if (this.canCaptureBrowserSignals()) {
      this.attachBrowserMonitors();
    } else {
      this.detachBrowserMonitors();
    }
  }

  private startDeviceHealthMonitor(): void {
    if (!this.isBrowser || this.deviceHealthTimer) {
      return;
    }

    this.deviceHealthTimer = setInterval(() => {
      this.checkLiveDeviceHealth();
    }, 1000);
  }

  private stopDeviceHealthMonitor(): void {
    if (this.deviceHealthTimer) {
      clearInterval(this.deviceHealthTimer);
      this.deviceHealthTimer = null;
    }

    this.clearDeviceGraceTimer();
  }

  private clearDeviceGraceTimer(): void {
    if (!this.deviceGraceTimer) {
      return;
    }

    clearTimeout(this.deviceGraceTimer);
    this.deviceGraceTimer = null;
  }

  private checkLiveDeviceHealth(): void {
    if (!this.isInProgress() || !this.canEnd()) {
      this.clearDeviceGraceTimer();
      return;
    }

    if (this.requestingPerms()) {
      return;
    }

    if (!this.permChecked() && !this.hasRequiredPermissions()) {
      return;
    }

    const cameraStream = this.cameraStream();
    const captureStream = this.mediaStream;
    const hasCamera = this.hasLiveEnabledTrack(cameraStream, 'video');
    const hasMic =
      this.hasLiveEnabledTrack(cameraStream, 'audio') ||
      this.hasLiveEnabledTrack(captureStream, 'audio');

    if (hasCamera && hasMic) {
      this.clearDeviceGraceTimer();

      // Watchdog: auto-restart mic capture if it should be running but isn't.
      // This covers race conditions where startLiveCapture failed silently,
      // or where the pause/resume cycle lost the recorder.
      if (
        !this.liveCapturing() &&
        !this.pausedCaptureForPlayback &&
        !this.audioPlaying() &&
        this.isRealtimeConnected()
      ) {
        void this.startLiveCapture();
      }

      this.maybeWarnMissingTranscript();
      return;
    }

    if (this.deviceGraceTimer) {
      return;
    }

    this.error.set('Camera or microphone disconnected. Restore both within 5 seconds or the interview will end automatically.');
    this.deviceGraceTimer = setTimeout(() => {
      this.deviceGraceTimer = null;
      this.forceAutoEndSession('device_interruption_grace_elapsed').subscribe();
    }, this.deviceGracePeriodMs);
  }

  private canCaptureBrowserSignals(): boolean {
    return this.canEnd();
  }

  private attachBrowserMonitors(): void {
    if (!this.isBrowser || this.browserMonitorsAttached) return;
    document.addEventListener('visibilitychange', this.handleVisibilityChange);
    window.addEventListener('blur', this.handleWindowBlur);
    window.addEventListener('copy', this.handleClipboardInteraction);
    window.addEventListener('paste', this.handleClipboardInteraction);
    window.addEventListener('contextmenu', this.handleContextMenu);
    this.browserMonitorsAttached = true;
  }

  private detachBrowserMonitors(): void {
    if (!this.isBrowser || !this.browserMonitorsAttached) return;
    document.removeEventListener('visibilitychange', this.handleVisibilityChange);
    window.removeEventListener('blur', this.handleWindowBlur);
    window.removeEventListener('copy', this.handleClipboardInteraction);
    window.removeEventListener('paste', this.handleClipboardInteraction);
    window.removeEventListener('contextmenu', this.handleContextMenu);
    this.browserMonitorsAttached = false;
  }

  private readonly handleVisibilityChange = (): void => {
    if (!this.isBrowser || !this.canCaptureBrowserSignals()) return;
    if (document.visibilityState === 'hidden') this.bumpBrowserCounter('tabSwitchCount');
  };

  private readonly handleWindowBlur = (): void => {
    if (!this.canCaptureBrowserSignals()) return;
    this.bumpBrowserCounter('focusLossCount');
  };

  private readonly handleClipboardInteraction = (): void => {
    if (!this.canCaptureBrowserSignals()) return;
    this.bumpBrowserCounter('copyPasteCount');
  };

  private readonly handleContextMenu = (): void => {
    if (!this.canCaptureBrowserSignals()) return;
    this.bumpBrowserCounter('rightClickCount');
  };

  private bumpBrowserCounter(key: BrowserCounterKey): void {
    this.browserCounters.update((c) => ({ ...c, [key]: c[key] + 1 }));
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) return err.message;
    return fallback;
  }

  private canUseBrowserSpeechSynthesis(): boolean {
    return (
      this.isBrowser &&
      typeof SpeechSynthesisUtterance !== 'undefined' &&
      !!(window as Window & { speechSynthesis?: SpeechSynthesis }).speechSynthesis
    );
  }

  private hasLiveEnabledTrack(stream: MediaStream | null, kind: 'audio' | 'video'): boolean {
    if (!stream) {
      return false;
    }

    const tracks = kind === 'audio' ? stream.getAudioTracks() : stream.getVideoTracks();
    return tracks.some((track) => track.readyState === 'live' && track.enabled);
  }

  private maybeWarnMissingTranscript(): void {
    if (!this.isInProgress() || !this.liveCapturing() || this.noTranscriptWarningShown) {
      return;
    }

    if (!this.captureStartedAtMs) {
      return;
    }

    const now = Date.now();
    const baseline = this.lastTranscriptReceivedAtMs ?? this.captureStartedAtMs;
    if (now - baseline < this.noTranscriptWarningDelayMs) {
      return;
    }

    this.noTranscriptWarningShown = true;
    const warning =
      'Microphone is active, but no transcript is coming back yet. Check your selected mic and live connection status.';
    this.realtimeError.set(warning);
    this.pushTranscript('system', warning);
  }

  private startVideoFrameStreaming(): void {
    if (!this.isBrowser || this.frameCaptureTimer || !this.isRealtimeConnected()) return;
    this.cameraMonitoringActive.set(true);

    this.frameCaptureTimer = setInterval(() => {
      const socket = this.realtimeSocket;
      const stream = this.cameraStream();
      if (!socket || socket.readyState !== WebSocket.OPEN || !stream) return;

      const video = this.arenaVideoRef?.nativeElement ?? this.previewVideoRef?.nativeElement;
      if (!video || video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || video.videoWidth <= 0 || video.videoHeight <= 0) {
        return;
      }

      if (!this.frameCanvas) this.frameCanvas = document.createElement('canvas');
      const canvas = this.frameCanvas;
      canvas.width = video.videoWidth;
      canvas.height = video.videoHeight;
      const context = canvas.getContext('2d');
      if (!context) return;

      context.drawImage(video, 0, 0, canvas.width, canvas.height);
      const dataUrl = canvas.toDataURL('image/jpeg', 0.45);
      const base64Frame = dataUrl.split(',', 2)[1] ?? '';
      if (!base64Frame) return;

      socket.sendVideoFrame(base64Frame);
    }, 650);
  }

  private stopVideoFrameStreaming(): void {
    this.cameraMonitoringActive.set(false);
    if (this.frameCaptureTimer) {
      clearInterval(this.frameCaptureTimer);
      this.frameCaptureTimer = null;
    }
  }
}
