import { Component, OnInit, OnDestroy, inject, ViewChild, ElementRef, AfterViewChecked, ChangeDetectorRef } from '@angular/core';
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
  templateUrl: './chat-window.page.html',
  styleUrl: './chat-window.page.css'
})
export class ChatWindowPage implements OnInit, OnDestroy, AfterViewChecked {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private chatService = inject(ChatService);
  private authService = inject(AuthService);
  private cdr = inject(ChangeDetectorRef);

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

    this.sub = new Subscription();
    this.sub.add(
      this.chatService.incomingMessage$.subscribe(msg => {
        if (msg && msg.conversationId === this.conversationId) {
          // If we didn't send it, it might not be in the list, or it's new
          const exists = this.messages.find(m => m.id === msg.id);
          if (!exists) {
            this.messages.push(msg);
            this.scrollToBottom();
            if (msg.senderId !== this.currentUserId) {
               this.markAsRead();
            }
            this.cdr.detectChanges();
          }
        }
      })
    );

    this.sub.add(
      this.chatService.messagesRead$.subscribe(convId => {
        if (convId === this.conversationId) {
          this.messages.forEach(m => {
            if (m.senderId === this.currentUserId) m.isRead = true;
          });
          this.cdr.detectChanges();
        }
      })
    );
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
        this.cdr.detectChanges();
        setTimeout(() => this.scrollToBottom(), 100);
      },
      error: (err) => {
        console.error('Failed to load messages', err);
        this.loading = false;
        this.cdr.detectChanges();
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
