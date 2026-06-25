import { Component, OnInit, inject, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
import { ChatService, ChatConversationDto } from '../../../../core/services/chat.service';
import { Subscription } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';

@Component({
  selector: 'app-inbox-page',
  standalone: true,
  imports: [CommonModule, RouterModule],
  template: `
    <div class="container mx-auto p-4 max-w-4xl">
      <h1 class="text-2xl font-bold mb-6">Inbox</h1>

      <div *ngIf="loading" class="text-center py-8">
        <span class="loading loading-spinner loading-lg"></span>
      </div>

      <div *ngIf="!loading && conversations.length === 0" class="text-center py-12 bg-base-200 rounded-lg">
        <h3 class="text-lg font-medium">No messages yet</h3>
        <p class="text-base-content/70 mt-2">When you connect with someone, your conversation will appear here.</p>
      </div>

      <div *ngIf="!loading && conversations.length > 0" class="flex flex-col gap-4">
        <div 
          *ngFor="let conv of conversations" 
          class="card bg-base-100 shadow-sm border border-base-200 cursor-pointer hover:border-primary transition-colors"
          (click)="openConversation(conv.id)"
        >
          <div class="card-body p-4 flex flex-row items-center gap-4">
            <div class="avatar placeholder">
              <div class="bg-neutral text-neutral-content rounded-full w-12">
                <span class="text-xl">{{ conv.otherParticipantName.charAt(0) | uppercase }}</span>
              </div>
            </div>
            
            <div class="flex-1 min-w-0">
              <div class="flex justify-between items-center mb-1">
                <h3 class="font-semibold text-lg truncate">{{ conv.otherParticipantName }}</h3>
                <span class="text-xs text-base-content/70 whitespace-nowrap ml-2">
                  {{ conv.lastMessageAtUtc | date:'short' }}
                </span>
              </div>
              <p class="text-sm text-base-content/70 truncate" [class.font-semibold]="conv.unreadCount > 0">
                {{ conv.lastMessagePreview || 'No messages yet' }}
              </p>
            </div>

            <div *ngIf="conv.unreadCount > 0" class="badge badge-primary">
              {{ conv.unreadCount }}
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: []
})
export class InboxPage implements OnInit, OnDestroy {
  private chatService = inject(ChatService);
  private authService = inject(AuthService);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  
  conversations: ChatConversationDto[] = [];
  loading = true;
  private sub?: Subscription;

  ngOnInit() {
    this.route.queryParams.subscribe(params => {
      const receiverId = Number(params['receiverId']);
      if (receiverId) {
        this.chatService.getOrCreateConversation(receiverId).subscribe({
          next: (conv) => {
            this.openConversation(conv.id);
          },
          error: (err) => {
            console.error('Failed to create conversation', err);
            this.loadConversations();
          }
        });
      } else {
        this.loadConversations();
      }
    });

    // Re-fetch conversations if a new message arrives so preview/unread updates
    this.sub = this.chatService.incomingMessage$.subscribe(msg => {
      if (msg) {
        this.loadConversations();
      }
    });
  }

  ngOnDestroy() {
    this.sub?.unsubscribe();
  }

  loadConversations() {
    this.chatService.getConversations().subscribe({
      next: (data) => {
        this.conversations = data;
        this.loading = false;
      },
      error: (err) => {
        console.error('Failed to load conversations', err);
        this.loading = false;
      }
    });
  }

  openConversation(id: number) {
    const role = this.authService.currentUser()?.role;
    if (role === 'Recruiter' || role === 'Admin') {
      this.router.navigate(['/recruiter/chat', id]);
    } else {
      this.router.navigate(['/candidate/chat', id]);
    }
  }
}
