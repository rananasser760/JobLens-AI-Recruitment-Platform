import { Component, OnInit, inject, OnDestroy, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
import { ChatService, ChatConversationDto } from '../../../../core/services/chat.service';
import { Subscription } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';

@Component({
  selector: 'app-inbox-page',
  standalone: true,
  imports: [CommonModule, RouterModule],
  templateUrl: './inbox.page.html',
  styleUrl: './inbox.page.css'
})
export class InboxPage implements OnInit, OnDestroy {
  private chatService = inject(ChatService);
  private authService = inject(AuthService);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private cdr = inject(ChangeDetectorRef);
  
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
        this.cdr.detectChanges();
      },
      error: (err) => {
        console.error('Failed to load conversations', err);
        this.loading = false;
        this.cdr.detectChanges();
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
