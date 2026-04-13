import { CommonModule, DatePipe, DecimalPipe, isPlatformBrowser } from '@angular/common';
import {
  AfterViewChecked,
  Component,
  computed,
  ElementRef,
  inject,
  OnDestroy,
  PLATFORM_ID,
  signal,
  ViewChild
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { InterviewsService } from '../../../interviews/interviews.service';
import {
  InterviewRealtimeService,
  RealtimeSocketLike
} from '../../../interviews/interview-realtime.service';
import { InterviewQuestionDto, InterviewSessionDto } from '../../../../core/models/interview.model';

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
  imports: [CommonModule, RouterLink, DatePipe, DecimalPipe],
  templateUrl: './candidate-interview-session.page.html',
  styleUrls: ['./candidate-interview-session.page.css'],
  host: { class: 'interview-host' }
})
export class CandidateInterviewSessionPage implements OnDestroy, AfterViewChecked {
  private readonly route = inject(ActivatedRoute);
  private readonly interviewsService = inject(InterviewsService);
  private readonly interviewRealtime = inject(InterviewRealtimeService);
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
  private timerInterval: ReturnType<typeof setInterval> | null = null;
  private shouldScrollTranscript = false;

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
  readonly hasTranscripts = computed(() => this.transcriptEntries().length > 0);
  readonly formattedTimer = computed(() => {
    const s = this.sessionTimerSeconds();
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return `${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
  });

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
      this.interviewPhase.set('error');
      return;
    }
    const status = this.normalizeStatus(session.status);
    if (status.includes('scheduled') || status.includes('draft')) {
      this.interviewPhase.set('pre-check');
    } else if (status.includes('progress') || status.includes('started')) {
      this.interviewPhase.set('in-progress');
      this.startTimer();
      this.syncRealtimeState();
    } else if (status.includes('complet') || status.includes('ended')) {
      this.interviewPhase.set('completed');
      this.stopTimer();
    } else if (status.includes('cancel')) {
      this.interviewPhase.set('error');
      this.error.set('This interview session has been cancelled.');
    } else {
      this.interviewPhase.set('pre-check');
    }
  }

  isInProgress(): boolean {
    const p = this.interviewPhase();
    return p === 'in-progress' || p === 'completing';
  }

  canStart(): boolean {
    const status = this.normalizeStatus(this.session()?.status);
    return status.includes('scheduled') || status.includes('draft');
  }

  canEnd(): boolean {
    const status = this.normalizeStatus(this.session()?.status);
    return status.includes('progress') || status.includes('started');
  }

  // ─── Permissions ───
  async requestPermissions(): Promise<void> {
    if (!this.isBrowser) return;
    this.requestingPerms.set(true);
    this.permChecked.set(false);

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: true });
      this.cameraStream.set(stream);
      this.permissionsGranted.set({ camera: true, mic: true });
    } catch {
      // Try audio only
      try {
        const audioStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
        this.cameraStream.set(audioStream);
        this.permissionsGranted.set({ camera: false, mic: true });
      } catch {
        this.permissionsGranted.set({ camera: false, mic: false });
      }
    } finally {
      this.permChecked.set(true);
      this.requestingPerms.set(false);
    }
  }

  private attachCameraToVideoElement(): void {
    const stream = this.cameraStream();
    if (!stream) return;

    const targets: (ElementRef<HTMLVideoElement> | undefined)[] = [
      this.previewVideoRef,
      this.arenaVideoRef
    ];
    for (const ref of targets) {
      if (ref?.nativeElement && ref.nativeElement.srcObject !== stream) {
        ref.nativeElement.srcObject = stream;
      }
    }
  }

  // ─── Enter Interview (Pre-check → In-progress) ───
  enterInterview(): void {
    if (this.acting() || !this.canStart() || !this.permissionsGranted().mic) return;
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
        next: () => {
          this.interviewPhase.set('in-progress');
          this.startTimer();
          this.load();
          // Auto-connect realtime and start mic after brief delay
          setTimeout(() => {
            this.ensureRealtimeConnection(false);
            setTimeout(() => {
              if (this.isRealtimeConnected()) {
                void this.startLiveCapture();
              }
            }, 1500);
          }, 800);
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
          this.stopLiveCapture();
          this.disconnectRealtime(true);
          this.stopTimer();
          this.releaseCameraStream();
          this.session.set(res.data);
          this.interviewPhase.set('completed');
        },
        error: (err: unknown) => {
          this.interviewPhase.set('in-progress');
          this.error.set(this.mapError(err, 'Unable to end the interview session right now.'));
        }
      });
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
      this.realtimeError.set('Connect live mode before starting microphone capture.');
      return;
    }
    if (this.liveCapturing()) return;

    try {
      // Reuse existing camera stream's audio track if available
      let stream = this.cameraStream();
      if (!stream || !stream.getAudioTracks().length) {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      }

      const preferredMimeTypes = [
        'audio/webm;codecs=opus',
        'audio/webm',
        'audio/ogg;codecs=opus'
      ];
      const selectedMimeType = preferredMimeTypes.find((t) => MediaRecorder.isTypeSupported(t));
      const recorder = selectedMimeType
        ? new MediaRecorder(stream, { mimeType: selectedMimeType })
        : new MediaRecorder(stream);

      recorder.ondataavailable = (event: BlobEvent) => {
        const socket = this.realtimeSocket;
        if (!socket || socket.readyState !== WebSocket.OPEN || !event.data || event.data.size === 0)
          return;

        void event.data
          .arrayBuffer()
          .then((buffer) => {
            if (this.realtimeSocket && this.realtimeSocket.readyState === WebSocket.OPEN) {
              this.realtimeSocket.send(buffer);
            }
          })
          .catch(() => {});
      };

      recorder.start(1500);
      this.mediaRecorder = recorder;
      this.mediaStream = stream;
      this.liveCapturing.set(true);
      this.realtimeError.set(null);
      this.pushTranscript('system', 'Live microphone streaming started.');
    } catch (err: unknown) {
      this.realtimeError.set(this.mapError(err, 'Unable to access your microphone.'));
      this.releaseMediaResources();
    }
  }

  private stopLiveCapture(): void {
    if (!this.liveCapturing()) {
      this.releaseMediaResources();
      return;
    }
    if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive') {
      this.mediaRecorder.stop();
    }
    this.releaseMediaResources();
    this.pushTranscript('system', 'Live microphone streaming stopped.');
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
      // Auto-start mic capture once connected
      setTimeout(() => void this.startLiveCapture(), 500);
    };

    socket.onmessage = (event: MessageEvent) => {
      this.handleRealtimePayload(event.data);
    };

    socket.onerror = () => {
      this.realtimeError.set('Live connection encountered an error.');
    };

    socket.onclose = () => {
      this.realtimeSocket = null;
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
    this.clearReconnectTimer();
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
      this.enqueueAudio(payload);
      return;
    }
    if (payload instanceof ArrayBuffer) {
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
    if (parsed['type'] !== 'transcript') return;

    const userText = typeof parsed['user'] === 'string' ? parsed['user'].trim() : '';
    const aiText = typeof parsed['ai'] === 'string' ? parsed['ai'].trim() : '';
    const isComplete = !!parsed['is_complete'];

    if (userText && userText !== '(Silence / Inaudible)' && userText !== '(System)') {
      this.pushTranscript('candidate', userText);
    }
    if (userText === '(System)' && aiText) {
      this.pushTranscript('system', aiText);
    } else if (aiText) {
      this.pushTranscript('assistant', aiText);
    }

    if (isComplete) {
      this.stopLiveCapture();
      // Auto-end the session
      const id = this.sessionId();
      if (id && this.canEnd()) {
        this.pushTranscript('system', 'AI interview completed. Generating your report...');
        this.interviewPhase.set('completing');
        this.interviewsService.end(id).subscribe({
          next: (res) => {
            this.stopTimer();
            this.releaseCameraStream();
            this.disconnectRealtime(true);
            this.session.set(res.data);
            this.interviewPhase.set('completed');
          },
          error: () => {
            this.interviewPhase.set('in-progress');
          }
        });
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
    this.audioQueue.push(audioBlob);
    if (this.audioPlaying()) return;
    this.playNextAudio();
  }

  private playNextAudio(): void {
    const next = this.audioQueue.shift();
    if (!next) {
      this.audioPlaying.set(false);
      return;
    }
    const url = URL.createObjectURL(next);
    const player = new Audio(url);
    this.audioPlayer = player;
    this.audioPlaying.set(true);

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

  // ─── Lifecycle ───
  ngOnDestroy(): void {
    this.detachBrowserMonitors();
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
}
