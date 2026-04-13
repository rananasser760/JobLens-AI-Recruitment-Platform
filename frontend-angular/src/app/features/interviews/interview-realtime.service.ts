import { Injectable } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  LogLevel
} from '@microsoft/signalr';

import { environment } from '../../../environments/environment';

export interface RealtimeSocketLike {
  readonly readyState: number;
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent<string>) => void) | null;
  onerror: ((event: Event) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  send(data: Blob | ArrayBuffer | string): void;
  close(): void;
  requestOpeningPrompt(): void;
  sendVideoFrame(base64Frame: string): void;
}

class SignalRInterviewSocket implements RealtimeSocketLike {
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent<string>) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;

  private readonly connection: HubConnection;
  private audioSequence = 0;
  private manualClose = false;
  private currentState: number = WebSocket.CONNECTING;

  constructor(private readonly sessionId: number) {
    this.connection = new HubConnectionBuilder()
      .withUrl(environment.realtimeHubUrl)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    this.registerHandlers();
    void this.start();
  }

  get readyState(): number {
    return this.currentState;
  }

  send(data: Blob | ArrayBuffer | string): void {
    if (this.currentState !== WebSocket.OPEN) {
      return;
    }

    void this.sendAudio(data);
  }

  close(): void {
    this.manualClose = true;
    this.currentState = WebSocket.CLOSING;
    void this.connection.stop().finally(() => {
      this.currentState = WebSocket.CLOSED;
      this.onclose?.(new CloseEvent('close'));
    });
  }

  requestOpeningPrompt(): void {
    if (this.currentState !== WebSocket.OPEN) {
      return;
    }
    void this.connection.invoke('RequestOpeningPrompt', this.sessionId).catch(() => {});
  }

  sendVideoFrame(base64Frame: string): void {
    if (this.currentState !== WebSocket.OPEN) {
      return;
    }
    void this.connection.invoke('SubmitVideoFrame', this.sessionId, base64Frame).catch(() => {});
  }

  private registerHandlers(): void {
    this.connection.on('audioProcessed', (payload: unknown) => {
      const data = this.unwrap(payload);
      if (!data || typeof data !== 'object' || Array.isArray(data)) {
        return;
      }

      const typed = data as Record<string, unknown>;
      const userText = typeof typed['transcript'] === 'string' ? typed['transcript'] : '';
      const aiText = typeof typed['reply'] === 'string' ? typed['reply'] : '';
      const isComplete = !!typed['isComplete'];
      const message = JSON.stringify({
        type: 'transcript',
        user: userText,
        ai: aiText,
        is_complete: isComplete
      });

      this.onmessage?.(new MessageEvent('message', { data: message }));
    });

    this.connection.on('videoProcessed', (payload: unknown) => {
      const data = this.unwrap(payload);
      const entries = Array.isArray(data)
        ? data.filter((item): item is string => typeof item === 'string')
        : [];
      if (entries.length === 0) {
        return;
      }

      this.onmessage?.(
        new MessageEvent('message', {
          data: JSON.stringify({
            type: 'transcript',
            user: '(System)',
            ai: `Proctoring notice: ${entries.join(' | ')}`,
            is_complete: false
          })
        })
      );
    });

    this.connection.on('interviewCompleted', (payload: unknown) => {
      const data = this.unwrap(payload);
      let messageText = 'Interview completed.';
      if (data && typeof data === 'object' && !Array.isArray(data)) {
        const typed = data as Record<string, unknown>;
        if (typeof typed['message'] === 'string') {
          messageText = typed['message'];
        }
      }

      this.onmessage?.(
        new MessageEvent('message', {
          data: JSON.stringify({
            type: 'transcript',
            user: '(System)',
            ai: messageText,
            is_complete: true
          })
        })
      );
    });

    this.connection.onreconnecting(() => {
      this.currentState = WebSocket.CONNECTING;
    });

    this.connection.onreconnected(async () => {
      this.currentState = WebSocket.OPEN;
      await this.connection.invoke('JoinSession', this.sessionId);
      this.onopen?.(new Event('open'));
    });

    this.connection.onclose(() => {
      this.currentState = WebSocket.CLOSED;
      if (this.manualClose) {
        return;
      }

      this.onclose?.(new CloseEvent('close'));
    });
  }

  private async start(): Promise<void> {
    try {
      await this.connection.start();
      await this.connection.invoke('JoinSession', this.sessionId);
      this.currentState = WebSocket.OPEN;
      this.onopen?.(new Event('open'));
    } catch {
      this.currentState = WebSocket.CLOSED;
      this.onerror?.(new Event('error'));
      this.onclose?.(new CloseEvent('close'));
    }
  }

  private async sendAudio(data: Blob | ArrayBuffer | string): Promise<void> {
    const base64Audio = await this.toBase64(data);
    this.audioSequence += 1;
    await this.connection.invoke('SubmitAudio', this.sessionId, base64Audio, this.audioSequence);
  }

  private async toBase64(data: Blob | ArrayBuffer | string): Promise<string> {
    if (typeof data === 'string') {
      return btoa(data);
    }

    const blob = data instanceof Blob ? data : new Blob([data]);
    return new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onloadend = () => {
        const dataUrl = reader.result as string;
        const base64 = dataUrl.split(',')[1];
        resolve(base64 ?? '');
      };
      reader.onerror = reject;
      reader.readAsDataURL(blob);
    });
  }

  private arrayBufferToBase64(buffer: ArrayBuffer): string {
    let binary = '';
    const bytes = new Uint8Array(buffer);
    for (const byte of bytes) {
      binary += String.fromCharCode(byte);
    }
    return btoa(binary);
  }

  private unwrap(payload: unknown): unknown {
    if (!payload || typeof payload !== 'object') {
      return null;
    }

    const candidate = payload as { data?: unknown };
    return candidate.data ?? payload;
  }
}

@Injectable({ providedIn: 'root' })
export class InterviewRealtimeService {
  connectInterview(interviewSessionId: number): RealtimeSocketLike {
    return new SignalRInterviewSocket(interviewSessionId);
  }

  close(socket: RealtimeSocketLike): void {
    socket.close();
  }
}
