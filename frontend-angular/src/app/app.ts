import { Component, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { RouterOutlet } from '@angular/router';
import { LoadingService } from './core/services/loading.service';
import { LoadingSpinnerComponent } from './shared/components/loading-spinner/loading-spinner.component';
import { ChatService } from './core/services/chat.service';
import { TokenStoreService } from './core/auth/token-store.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, LoadingSpinnerComponent],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly loadingService = inject(LoadingService);
  private readonly chatService = inject(ChatService);
  private readonly tokenStore = inject(TokenStoreService);
  private readonly platformId = inject(PLATFORM_ID);

  readonly globalLoading = this.loadingService.isLoading;

  constructor() {
    if (isPlatformBrowser(this.platformId) && this.tokenStore.getAccessToken()) {
      this.chatService.startConnection().catch((err) => {
        console.warn('Chat connection could not be established on startup:', err);
      });
    }
  }
}
