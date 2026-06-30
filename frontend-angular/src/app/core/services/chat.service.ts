import { Injectable, inject, NgZone } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import * as signalR from '@microsoft/signalr';
import { TokenStoreService } from '../auth/token-store.service';

export interface ChatConversationDto {
  id: number;
  otherParticipantId: number;
  otherParticipantName: string;
  lastMessagePreview: string;
  lastMessageAtUtc: string;
  unreadCount: number;
}

export interface ChatAttachmentDto {
  id: number;
  fileName: string;
  fileUrl: string;
  fileType: string;
  fileSize: number;
}

export interface ChatMessageDto {
  id: number;
  conversationId: number;
  senderId: number;
  senderName: string;
  content: string;
  isRead: boolean;
  sentAtUtc: string;
  attachments: ChatAttachmentDto[];
}

export interface SendMessageDto {
  receiverId: number;
  content: string;
  attachmentIds?: number[];
}

@Injectable({
  providedIn: 'root'
})
export class ChatService {
  private http = inject(HttpClient);
  private tokenStore = inject(TokenStoreService);
  private ngZone = inject(NgZone);
  private hubConnection: signalR.HubConnection | null = null;
  private readonly baseUrl = `${environment.apiBaseUrl}${environment.apiPrefix}/chat`;
  private readonly hubUrl = `${environment.apiBaseUrl}/hubs/chat`;

  private messagesSubject = new BehaviorSubject<ChatMessageDto | null>(null);
  public incomingMessage$ = this.messagesSubject.asObservable();
  
  private messagesReadSubject = new Subject<number>();
  public messagesRead$ = this.messagesReadSubject.asObservable();

  public async startConnection(): Promise<void> {
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      return;
    }

    const token = this.tokenStore.getAccessToken();
    if (!token) return;

    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl, {
        accessTokenFactory: () => this.tokenStore.getAccessToken() ?? '',
        skipNegotiation: false,
        transport: signalR.HttpTransportType.WebSockets
      })
      .withAutomaticReconnect()
      .build();

    this.hubConnection.on('ReceiveMessage', (message: ChatMessageDto) => {
      this.ngZone.run(() => {
        this.messagesSubject.next(message);
      });
    });

    this.hubConnection.on('MessagesRead', (conversationId: number) => {
      this.ngZone.run(() => {
        this.messagesReadSubject.next(conversationId);
      });
    });

    try {
      await this.hubConnection.start();
      console.log('Chat SignalR connection established.');
    } catch (err) {
      console.error('Error establishing Chat SignalR connection:', err);
    }
  }

  public stopConnection(): void {
    if (this.hubConnection) {
      this.hubConnection.stop();
      this.hubConnection = null;
    }
  }

  public getConversations(): Observable<ChatConversationDto[]> {
    return this.http.get<ChatConversationDto[]>(`${this.baseUrl}/conversations`);
  }

  public getOrCreateConversation(otherUserId: number): Observable<ChatConversationDto> {
    return this.http.post<ChatConversationDto>(`${this.baseUrl}/conversations/with/${otherUserId}`, {});
  }

  public getMessages(conversationId: number, skip = 0, take = 50): Observable<ChatMessageDto[]> {
    return this.http.get<ChatMessageDto[]>(`${this.baseUrl}/conversations/${conversationId}/messages?skip=${skip}&take=${take}`);
  }

  public getUnreadCount(): Observable<{ count: number }> {
    return this.http.get<{ count: number }>(`${this.baseUrl}/unread-count`);
  }

  public markAsRead(conversationId: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/conversations/${conversationId}/read`, {});
  }

  public async sendMessageRealtime(dto: SendMessageDto): Promise<void> {
    if (this.hubConnection?.state === signalR.HubConnectionState.Connected) {
      await this.hubConnection.invoke('SendMessage', dto);
    } else {
      throw new Error('SignalR connection is not active.');
    }
  }
}
