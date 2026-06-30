import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { LoadingService } from './core/services/loading.service';
import { LoadingSpinnerComponent } from './shared/components/loading-spinner/loading-spinner.component';
import { ChatService } from './core/services/chat.service';
import { TokenStoreService } from './core/auth/token-store.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, LoadingSpinnerComponent],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {
  private readonly loadingService = inject(LoadingService);
  private readonly chatService = inject(ChatService);
  private readonly tokenStore = inject(TokenStoreService);

  readonly globalLoading = this.loadingService.isLoading;

  constructor() {
    if (this.tokenStore.getAccessToken()) {
      this.chatService.startConnection();
    }
  }
}
