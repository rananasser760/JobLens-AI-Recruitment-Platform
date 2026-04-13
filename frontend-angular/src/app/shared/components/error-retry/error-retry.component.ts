import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';

@Component({
  selector: 'app-error-retry',
  imports: [CommonModule],
  templateUrl: './error-retry.component.html',
  styleUrl: './error-retry.component.css'
})
export class ErrorRetryComponent {
  @Input() message = 'Something went wrong.';
  @Input() actionLabel = 'Retry';
  @Input() busy = false;
  @Input() visible = true;

  @Output() retry = new EventEmitter<void>();

  onRetry(): void {
    if (this.busy) {
      return;
    }

    this.retry.emit();
  }
}