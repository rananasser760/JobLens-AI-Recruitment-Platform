import { Injectable, NgZone, inject } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { Observable, Subject, from, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TokenStoreService } from '../auth/token-store.service';
import { ApiClientService } from '../api/api-client.service';
import { ApiResponse } from '../models/api.model';

export interface ChatMessage {
  id: number;
  conversationId: number;
  senderId: number;
  senderName: string;
  content: string;
  isRead: boolean;
  sentAtUtc: string;
  attachments?: ChatAttachment[];
}

export interface ChatAttachment {
  id: number;
  fileName: string;
  fileUrl: string;
  fileType: string;
  fileSize: number;
}

export interface ChatConversation {
  id: number;
  otherParticipantId: number;
  otherParticipantName: string;
  lastMessagePreview: string;
  lastMessageAtUtc: string;
  unreadCount: number;
}

export interface SendMessageRealtimeDto {
  receiverId?: number;
  conversationId?: number;
  content: string;
  attachmentIds?: number[];
}

export type ChatMessageDto = ChatMessage;
export type ChatConversationDto = ChatConversation;

@Injectable({
  providedIn: 'root',
})
export class ChatService {
  private readonly api = inject(ApiClientService);
  private readonly tokenStore = inject(TokenStoreService);
  private readonly zone = inject(NgZone);

  private hubConnection: HubConnection | null = null;

  private readonly messageReceivedSubject = new Subject<ChatMessage>();
  public readonly messageReceived$ = this.messageReceivedSubject.asObservable();
  public readonly incomingMessage$ = this.messageReceivedSubject.asObservable();

  private readonly messagesReadSubject = new Subject<number>();
  public readonly messagesRead$ = this.messagesReadSubject.asObservable();

  private readonly typingIndicatorSubject = new Subject<{ userId: number; isTyping: boolean }>();
  public readonly typingIndicator$ = this.typingIndicatorSubject.asObservable();

  public async startConnection(): Promise<void> {
    if (this.hubConnection && this.hubConnection.state === HubConnectionState.Connected) {
      return;
    }

    const hubUrl = `${environment.apiBaseUrl}/hubs/chat`;
    this.hubConnection = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => this.tokenStore.getAccessToken() || '',
      })
      .withAutomaticReconnect()
      .configureLogging(environment.production ? LogLevel.None : LogLevel.Information)
      .build();

    this.hubConnection.on('ReceiveMessage', (msg: ChatMessage) => {
      this.zone.run(() => this.messageReceivedSubject.next(msg));
    });

    this.hubConnection.on('MessagesRead', (conversationId: number) => {
      this.zone.run(() => this.messagesReadSubject.next(conversationId));
    });

    this.hubConnection.on('UserTyping', (userId: number) => {
      this.zone.run(() => this.typingIndicatorSubject.next({ userId, isTyping: true }));
    });

    this.hubConnection.on('UserStoppedTyping', (userId: number) => {
      this.zone.run(() => this.typingIndicatorSubject.next({ userId, isTyping: false }));
    });

    await this.hubConnection.start();
  }

  public async stopConnection(): Promise<void> {
    if (this.hubConnection) {
      await this.hubConnection.stop();
      this.hubConnection = null;
    }
  }

  public async sendMessage(
    receiverId: number,
    content: string,
    attachmentIds?: number[],
  ): Promise<void> {
    if (!this.hubConnection || this.hubConnection.state !== HubConnectionState.Connected) {
      await this.startConnection();
    }
    await this.hubConnection!.invoke('SendMessage', {
      receiverId,
      content,
      attachmentIds: attachmentIds || [],
    });
  }

  public async sendMessageRealtime(payload: SendMessageRealtimeDto): Promise<void> {
    if (!this.hubConnection || this.hubConnection.state !== HubConnectionState.Connected) {
      await this.startConnection();
    }
    await this.hubConnection!.invoke('SendMessage', payload);
  }

  public markAsRead(conversationId: number): Observable<void> {
    return from(this.markAsReadAsync(conversationId));
  }

  public async markAsReadAsync(conversationId: number): Promise<void> {
    if (!this.hubConnection || this.hubConnection.state !== HubConnectionState.Connected) {
      await this.startConnection();
    }
    await this.hubConnection!.invoke('MarkAsRead', conversationId);
  }

  public async sendTyping(receiverId: number, isTyping: boolean): Promise<void> {
    if (!this.hubConnection || this.hubConnection.state !== HubConnectionState.Connected) return;
    await this.hubConnection.invoke('SendTypingIndicator', receiverId, isTyping);
  }

  public getConversations(): Observable<ChatConversation[]> {
    return this.api
      .get<ChatConversation[]>('/api/chat/conversations')
      .pipe(map((res: ApiResponse<ChatConversation[]>) => res.data ?? []));
  }

  public getOrCreateConversation(otherUserId: number): Observable<ChatConversation> {
    return this.api
      .post<ChatConversation>(`/api/chat/conversations/with/${otherUserId}`, {})
      .pipe(map((res: ApiResponse<ChatConversation>) => res.data as ChatConversation));
  }

  public getMessages(conversationId: number, skip = 0, take = 50): Observable<ChatMessage[]> {
    return this.api
      .get<
        ChatMessage[]
      >(`/api/chat/conversations/${conversationId}/messages?skip=${skip}&take=${take}`)
      .pipe(map((res: ApiResponse<ChatMessage[]>) => res.data ?? []));
  }

  public uploadAttachment(file: File): Observable<ApiResponse<ChatAttachment>> {
    const formData = new FormData();
    formData.append('file', file);
    return this.api.post<ChatAttachment>('/api/chat/attachments', formData);
  }
}
