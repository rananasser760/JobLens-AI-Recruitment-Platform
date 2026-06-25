import { Component, Input, Output, EventEmitter, inject, ViewChild, ElementRef, OnChanges, SimpleChanges, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ChatService, ChatMessageDto } from '../../../core/services/chat.service';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-chat-modal',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './chat-modal.component.html',
  styleUrl: './chat-modal.component.css'
})
export class ChatModalComponent implements OnChanges, OnDestroy {
  private readonly chatService = inject(ChatService);
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);

  @Input() open = false;
  @Input() receiverId!: number;
  @Input() receiverName = 'Candidate';

  @Output() close = new EventEmitter<void>();

  @ViewChild('messagesContainer') private messagesContainer!: ElementRef;

  conversationId!: number;
  messages: ChatMessageDto[] = [];
  newMessage = '';
  loading = true;
  sending = false;
  currentUserId!: number;

  private sub?: Subscription;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['open'] && this.open) {
      this.initChat();
    }
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  async initChat() {
    this.currentUserId = this.authService.currentUser()?.userId || 0;
    this.messages = [];
    this.newMessage = '';
    this.loading = true;
    this.sending = false;

    // Ensure connection is started
    await this.chatService.startConnection();

    // Subscribe to incoming messages if not already subscribed
    if (!this.sub) {
      this.sub = this.chatService.incomingMessage$.subscribe((msg) => {
        if (msg && msg.conversationId === this.conversationId) {
          const exists = this.messages.some((m) => m.id === msg.id);
          if (!exists) {
            this.messages.push(msg);
            this.scrollToBottom();
            if (msg.senderId !== this.currentUserId) {
              this.markAsRead();
            }
          }
        }
      });
    }

    if (this.receiverId) {
      this.chatService.getOrCreateConversation(this.receiverId).subscribe({
        next: (conv) => {
          this.conversationId = conv.id;
          this.loadMessages();
          this.markAsRead();
        },
        error: (err) => {
          console.error('Failed to get/create conversation', err);
          this.loading = false;
        }
      });
    } else {
      this.loading = false;
    }
  }

  loadMessages() {
    this.loading = true;
    this.chatService.getMessages(this.conversationId, 0, 100).subscribe({
      next: (data) => {
        this.messages = data;
        this.loading = false;
        this.scrollToBottom();
      },
      error: (err) => {
        console.error('Failed to load messages', err);
        this.loading = false;
      }
    });
  }

  markAsRead() {
    if (this.conversationId) {
      this.chatService.markAsRead(this.conversationId).subscribe();
    }
  }

  async sendMessage() {
    const text = this.newMessage.trim();
    if (!text || !this.receiverId) return;

    this.sending = true;
    try {
      await this.chatService.sendMessageRealtime({
        receiverId: this.receiverId,
        content: text
      });
      this.newMessage = '';
      this.scrollToBottom();
    } catch (err) {
      console.error('Failed to send message', err);
    } finally {
      this.sending = false;
    }
  }

  onClose() {
    this.close.emit();
  }

  onBackdropClick(event: MouseEvent) {
    if (event.target === event.currentTarget) {
      this.onClose();
    }
  }

  goToFullChat() {
    if (this.conversationId) {
      this.onClose();
      this.router.navigate(['/recruiter/chat', this.conversationId]);
    }
  }

  private scrollToBottom(): void {
    setTimeout(() => {
      try {
        if (this.messagesContainer) {
          const el = this.messagesContainer.nativeElement;
          el.scrollTop = el.scrollHeight;
        }
      } catch (err) {
        console.error('Failed to scroll messages container', err);
      }
    }, 100);
  }
}
