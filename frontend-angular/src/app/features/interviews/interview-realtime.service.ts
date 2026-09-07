import { Injectable, NgZone, inject } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TokenStoreService } from '../../core/auth/token-store.service';

export interface AudioProcessedEvent {
  transcript: string;
  reply: string;
  isComplete: boolean;
  score?: number;
  replyAudioBase64?: string;
  replyAudioMimeType?: string;
}

export interface ProctoringAlertEvent {
  eventType: string;
  severity: string;
  source: string;
  description: string;
}

export type VadState = 'idle' | 'listening' | 'speaking' | 'processing' | 'playing';

@Injectable({
  providedIn: 'root',
})
export class InterviewRealtimeService {
  private readonly tokenStore = inject(TokenStoreService);
  private readonly zone = inject(NgZone);

  private hubConnection: HubConnection | null = null;
  private audioContext: AudioContext | null = null;
  private mediaStream: MediaStream | null = null;
  private processorNode: ScriptProcessorNode | null = null;
  private inputSource: MediaStreamAudioSourceNode | null = null;

  private currentSequence = 1;
  private activeSessionId: number | null = null;

  // VAD Thresholds & Adaptive State
  private readonly SAMPLE_RATE = 16000;
  private readonly BUFFER_SIZE = 4096;
  private readonly SPEECH_START_THRESHOLD = 0.045;
  private readonly SPEECH_END_THRESHOLD = 0.018;
  private readonly HANGOVER_TIME_MS = 1400; // Time in silence before utterance cutoff
  private readonly MIN_SPEECH_DURATION_MS = 400; // Reject clicks/puffs

  private isSpeaking = false;
  private speechStartTime = 0;
  private lastActiveTime = 0;
  private audioChunks: Float32Array[] = [];

  // Observables
  private readonly vadStateSubject = new BehaviorSubject<VadState>('idle');
  public readonly vadState$ = this.vadStateSubject.asObservable();

  private readonly audioProcessedSubject = new Subject<AudioProcessedEvent>();
  public readonly audioProcessed$ = this.audioProcessedSubject.asObservable();

  private readonly proctoringEventSubject = new Subject<ProctoringAlertEvent>();
  public readonly proctoringEvent$ = this.proctoringEventSubject.asObservable();

  private readonly volumeLevelSubject = new BehaviorSubject<number>(0);
  public readonly volumeLevel$ = this.volumeLevelSubject.asObservable();

  public async connect(sessionId: number): Promise<void> {
    if (this.hubConnection && this.hubConnection.state === HubConnectionState.Connected) {
      if (this.activeSessionId === sessionId) return;
      await this.disconnect();
    }

    this.activeSessionId = sessionId;
    const hubUrl = `${environment.apiBaseUrl}/hubs/interview`;

    this.hubConnection = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => this.tokenStore.getAccessToken() || '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(environment.production ? LogLevel.None : LogLevel.Information)
      .build();

    this.registerServerEvents();

    await this.hubConnection.start();
    await this.hubConnection.invoke('JoinSession', sessionId);
  }

  public async disconnect(): Promise<void> {
    this.stopAudioCapture();

    if (this.hubConnection) {
      if (this.activeSessionId && this.hubConnection.state === HubConnectionState.Connected) {
        try {
          await this.hubConnection.invoke('LeaveSession', this.activeSessionId);
        } catch {
          // Socket might already be closing
        }
      }
      await this.hubConnection.stop();
      this.hubConnection = null;
    }

    this.activeSessionId = null;
    this.vadStateSubject.next('idle');
  }

  public async startAudioCapture(): Promise<void> {
    if (this.audioContext && this.audioContext.state === 'running') {
      return;
    }

    this.mediaStream = await navigator.mediaDevices.getUserMedia({
      audio: {
        channelCount: 1,
        sampleRate: this.SAMPLE_RATE,
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
      },
      video: false,
    });

    this.audioContext = new (
      window.AudioContext ||
      (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    )({
      sampleRate: this.SAMPLE_RATE,
    });

    if (this.audioContext.state === 'suspended') {
      await this.audioContext.resume();
    }

    this.inputSource = this.audioContext.createMediaStreamSource(this.mediaStream);
    this.processorNode = this.audioContext.createScriptProcessor(this.BUFFER_SIZE, 1, 1);

    this.processorNode.onaudioprocess = (e) => {
      const input = e.inputBuffer.getChannelData(0);
      this.processAudioFrame(input);
    };

    this.inputSource.connect(this.processorNode);
    this.processorNode.connect(this.audioContext.destination);

    this.vadStateSubject.next('listening');
  }

  public stopAudioCapture(): void {
    if (this.processorNode) {
      this.processorNode.disconnect();
      this.processorNode.onaudioprocess = null;
      this.processorNode = null;
    }

    if (this.inputSource) {
      this.inputSource.disconnect();
      this.inputSource = null;
    }

    if (this.audioContext) {
      this.audioContext.close();
      this.audioContext = null;
    }

    if (this.mediaStream) {
      this.mediaStream.getTracks().forEach((track) => track.stop());
      this.mediaStream = null;
    }

    this.isSpeaking = false;
    this.audioChunks = [];
    this.volumeLevelSubject.next(0);
  }

  public async submitVideoFrame(base64Frame: string): Promise<void> {
    if (
      !this.hubConnection ||
      this.hubConnection.state !== HubConnectionState.Connected ||
      !this.activeSessionId
    ) {
      return;
    }

    await this.hubConnection.invoke(
      'SubmitVideoFrame',
      this.activeSessionId,
      base64Frame,
      this.currentSequence,
    );
  }

  public async requestOpeningPrompt(): Promise<void> {
    if (
      !this.hubConnection ||
      this.hubConnection.state !== HubConnectionState.Connected ||
      !this.activeSessionId
    ) {
      return;
    }

    this.vadStateSubject.next('processing');
    await this.hubConnection.invoke('RequestOpeningPrompt', this.activeSessionId);
  }

  private processAudioFrame(samples: Float32Array): void {
    const rms = this.calculateRms(samples);
    this.volumeLevelSubject.next(Math.min(1, rms * 8));

    const now = performance.now();

    if (!this.isSpeaking) {
      // Check Speech Onset
      if (rms >= this.SPEECH_START_THRESHOLD) {
        this.isSpeaking = true;
        this.speechStartTime = now;
        this.lastActiveTime = now;
        this.audioChunks = [new Float32Array(samples)];
        this.zone.run(() => this.vadStateSubject.next('speaking'));
      }
    } else {
      // Candidate is actively speaking
      this.audioChunks.push(new Float32Array(samples));

      if (rms >= this.SPEECH_END_THRESHOLD) {
        this.lastActiveTime = now;
      } else {
        // Evaluate Hangover silence interval
        const silenceDuration = now - this.lastActiveTime;
        const totalSpeechDuration = now - this.speechStartTime;

        if (silenceDuration >= this.HANGOVER_TIME_MS) {
          if (totalSpeechDuration >= this.MIN_SPEECH_DURATION_MS) {
            this.finalizeUtterance();
          } else {
            // Noise burst / click - discard
            this.isSpeaking = false;
            this.audioChunks = [];
            this.zone.run(() => this.vadStateSubject.next('listening'));
          }
        }
      }
    }
  }

  private finalizeUtterance(): void {
    this.isSpeaking = false;
    this.zone.run(() => this.vadStateSubject.next('processing'));

    const mergedSamples = this.mergeAudioBuffers(this.audioChunks);
    this.audioChunks = [];

    const wavBlob = this.encodeWav(mergedSamples, this.SAMPLE_RATE);
    this.convertBlobToBase64(wavBlob).then((base64) => {
      this.sendAudioChunk(base64);
    });
  }

  private async sendAudioChunk(base64Payload: string): Promise<void> {
    if (
      !this.hubConnection ||
      this.hubConnection.state !== HubConnectionState.Connected ||
      !this.activeSessionId
    ) {
      this.zone.run(() => this.vadStateSubject.next('listening'));
      return;
    }

    try {
      await this.hubConnection.invoke(
        'SubmitAudio',
        this.activeSessionId,
        base64Payload,
        this.currentSequence++,
      );
    } catch (err) {
      console.error('Failed to stream audio chunk to SignalR Hub', err);
      this.zone.run(() => this.vadStateSubject.next('listening'));
    }
  }

  private registerServerEvents(): void {
    if (!this.hubConnection) return;

    this.hubConnection.on(
      'audioProcessed',
      (res: { success: boolean; data: AudioProcessedEvent }) => {
        this.zone.run(() => {
          if (res?.success && res.data) {
            this.audioProcessedSubject.next(res.data);
            if (res.data.replyAudioBase64) {
              this.playAssistantAudio(res.data.replyAudioBase64, res.data.replyAudioMimeType);
            } else {
              this.vadStateSubject.next('listening');
            }
          } else {
            this.vadStateSubject.next('listening');
          }
        });
      },
    );

    this.hubConnection.on(
      'videoProcessed',
      (res: { success: boolean; data: { events: ProctoringAlertEvent[] } }) => {
        this.zone.run(() => {
          if (res?.success && res.data?.events) {
            for (const ev of res.data.events) {
              this.proctoringEventSubject.next(ev);
            }
          }
        });
      },
    );
  }

  private playAssistantAudio(base64Audio: string, mimeType = 'audio/wav'): void {
    this.vadStateSubject.next('playing');
    const audioUrl = `data:${mimeType};base64,${base64Audio}`;
    const audio = new Audio(audioUrl);

    audio.onended = () => {
      this.zone.run(() => this.vadStateSubject.next('listening'));
    };

    audio.onerror = () => {
      this.zone.run(() => this.vadStateSubject.next('listening'));
    };

    audio.play().catch(() => {
      this.zone.run(() => this.vadStateSubject.next('listening'));
    });
  }

  private calculateRms(samples: Float32Array): number {
    let sum = 0;
    for (let i = 0; i < samples.length; i++) {
      sum += samples[i] * samples[i];
    }
    return Math.sqrt(sum / samples.length);
  }

  private mergeAudioBuffers(buffers: Float32Array[]): Float32Array {
    let totalLen = 0;
    for (const b of buffers) totalLen += b.length;
    const result = new Float32Array(totalLen);
    let offset = 0;
    for (const b of buffers) {
      result.set(b, offset);
      offset += b.length;
    }
    return result;
  }

  private encodeWav(samples: Float32Array, sampleRate: number): Blob {
    const buffer = new ArrayBuffer(44 + samples.length * 2);
    const view = new DataView(buffer);

    this.writeString(view, 0, 'RIFF');
    view.setUint32(4, 36 + samples.length * 2, true);
    this.writeString(view, 8, 'WAVE');
    this.writeString(view, 12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true); // PCM format
    view.setUint16(22, 1, true); // Mono
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * 2, true);
    view.setUint16(32, 2, true);
    view.setUint16(34, 16, true);
    this.writeString(view, 36, 'data');
    view.setUint32(40, samples.length * 2, true);

    let index = 44;
    for (let i = 0; i < samples.length; i++) {
      const s = Math.max(-1, Math.min(1, samples[i]));
      view.setInt16(index, s < 0 ? s * 0x8000 : s * 0x7fff, true);
      index += 2;
    }

    return new Blob([view], { type: 'audio/wav' });
  }

  private writeString(view: DataView, offset: number, string: string): void {
    for (let i = 0; i < string.length; i++) {
      view.setUint8(offset + i, string.charCodeAt(i));
    }
  }

  private convertBlobToBase64(blob: Blob): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onloadend = () => {
        const dataUrl = reader.result as string;
        resolve(dataUrl.split(',')[1]);
      };
      reader.onerror = reject;
      reader.readAsDataURL(blob);
    });
  }
}
