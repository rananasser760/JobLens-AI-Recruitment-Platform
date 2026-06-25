import { Component, OnInit, OnDestroy, inject, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ChatService, ChatMessageDto } from '../../../../core/services/chat.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { Subscription } from 'rxjs';

@Component({
  selector: 'app-chat-window-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="container mx-auto max-w-4xl h-[calc(100vh-80px)] flex flex-col pt-4 pb-4">
      
      <!-- Header -->
      <div class="bg-base-100 border-b border-base-200 p-4 flex items-center justify-between rounded-t-lg shadow-sm">
        <div class="flex items-center gap-3">
          <button class="btn btn-ghost btn-sm btn-circle" (click)="goBack()">
            <i class="fas fa-arrow-left"></i>
          </button>
          <div>
            <h2 class="font-bold text-lg">Conversation</h2>
          </div>
        </div>
      </div>

      <!-- Messages Area -->
      <div class="flex-1 bg-base-200 overflow-y-auto p-4" #messagesContainer>
        <div *ngIf="loading" class="text-center py-8">
          <span class="loading loading-spinner loading-md"></span>
        </div>

        <div *ngFor="let msg of messages" class="chat" [class.chat-end]="msg.senderId === currentUserId" [class.chat-start]="msg.senderId !== currentUserId">
          <div class="chat-header mb-1">
            {{ msg.senderName }}
            <time class="text-xs opacity-50 ml-1">{{ msg.sentAtUtc | date:'shortTime' }}</time>
          </div>
          <div class="chat-bubble" [class.chat-bubble-primary]="msg.senderId === currentUserId">
            {{ msg.content }}
            <div *ngIf="msg.attachments && msg.attachments.length" class="mt-2 flex flex-col gap-1">
              <a *ngFor="let att of msg.attachments" [href]="att.fileUrl" target="_blank" class="btn btn-xs btn-outline">
                <i class="fas fa-paperclip mr-1"></i> {{ att.fileName }}
              </a>
            </div>
          </div>
          <div class="chat-footer opacity-50 text-xs mt-1" *ngIf="msg.senderId === currentUserId">
            {{ msg.isRead ? 'Read' : 'Delivered' }}
          </div>
        </div>
      </div>

      <!-- Input Area -->
      <div class="bg-base-100 p-4 rounded-b-lg shadow-sm border-t border-base-200">
        <form (ngSubmit)="sendMessage()" class="flex gap-2">
          <button type="button" class="btn btn-square btn-ghost" title="Attachments coming soon">
            <i class="fas fa-paperclip"></i>
          </button>
          <input 
            type="text" 
            [(ngModel)]="newMessage" 
            name="newMessage"
            placeholder="Type your message..." 
            class="input input-bordered flex-1"
            autocomplete="off"
            required
          />
          <button type="submit" class="btn btn-primary" [disabled]="!newMessage.trim() || sending">
            <span *ngIf="sending" class="loading loading-spinner loading-xs"></span>
            <i *ngIf="!sending" class="fas fa-paper-plane"></i>
          </button>
        </form>
      </div>

    </div>
  `,
  styles: []
})
export class ChatWindowPage implements OnInit, OnDestroy, AfterViewChecked {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private chatService = inject(ChatService);
  private authService = inject(AuthService);

  @ViewChild('messagesContainer') private messagesContainer!: ElementRef;

  conversationId!: number;
  messages: ChatMessageDto[] = [];
  currentUserId!: number;
  newMessage = '';
  loading = true;
  sending = false;

  private sub?: Subscription;

  ngOnInit() {
    this.currentUserId = this.authService.currentUser()?.userId || 0;
    
    this.route.paramMap.subscribe(params => {
      const idStr = params.get('conversationId');
      if (idStr) {
        this.conversationId = parseInt(idStr, 10);
        this.loadMessages();
        this.markAsRead();
      }
    });

    this.sub = this.chatService.incomingMessage$.subscribe(msg => {
      if (msg && msg.conversationId === this.conversationId) {
        // If we didn't send it, it might not be in the list, or it's new
        const exists = this.messages.find(m => m.id === msg.id);
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

  ngOnDestroy() {
    this.sub?.unsubscribe();
  }

  ngAfterViewChecked() {
    // Only scroll if we are near the bottom, or just forcefully for this demo
  }

  loadMessages() {
    this.loading = true;
    this.chatService.getMessages(this.conversationId, 0, 100).subscribe({
      next: (data) => {
        this.messages = data;
        this.loading = false;
        setTimeout(() => this.scrollToBottom(), 100);
      },
      error: (err) => {
        console.error('Failed to load messages', err);
        this.loading = false;
      }
    });
  }

  markAsRead() {
    this.chatService.markAsRead(this.conversationId).subscribe();
  }

  async sendMessage() {
    if (!this.newMessage.trim()) return;

    // We need the receiverId. We can infer it from the messages if there are any, 
    // or we'd need a GetConversation API call. Let's fetch it from the first message that isn't us.
    let receiverId = 0;
    const otherMsg = this.messages.find(m => m.senderId !== this.currentUserId);
    if (otherMsg) {
       receiverId = otherMsg.senderId;
    } else {
       // We might not have a receiverId easily available here if conversation has no messages
       // In a real app we'd load the conversation details too. 
       // For now, let's assume we fetch conversation detail first or we got here from a button.
       // As a fallback, we need to load conversation.
    }

    if (!receiverId) {
       // Let's get it from the conversation list
       this.chatService.getConversations().subscribe(async convs => {
          const conv = convs.find(c => c.id === this.conversationId);
          if (conv) {
             await this.executeSend(conv.otherParticipantId);
          }
       });
    } else {
       await this.executeSend(receiverId);
    }
  }

  private async executeSend(receiverId: number) {
    this.sending = true;
    try {
      await this.chatService.sendMessageRealtime({
        receiverId: receiverId,
        content: this.newMessage
      });
      this.newMessage = '';
      this.scrollToBottom();
    } catch (err) {
      console.error('Failed to send message', err);
    } finally {
      this.sending = false;
    }
  }

  goBack() {
    const role = this.authService.currentUser()?.role;
    if (role === 'Recruiter' || role === 'Admin') {
      this.router.navigate(['/recruiter/chat']);
    } else {
      this.router.navigate(['/candidate/chat']);
    }
  }

  private scrollToBottom(): void {
    try {
      this.messagesContainer.nativeElement.scrollTop = this.messagesContainer.nativeElement.scrollHeight;
    } catch (err) { }
  }
}
