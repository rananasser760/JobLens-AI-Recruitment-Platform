import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';

@Component({
  selector: 'app-confirm-dialog',
  imports: [CommonModule],
  templateUrl: './confirm-dialog.component.html',
  styleUrl: './confirm-dialog.component.css'
})
export class ConfirmDialogComponent {
  @Input() open = false;
  @Input() title = 'Please confirm';
  @Input() message = 'Are you sure you want to continue?';
  @Input() confirmLabel = 'Confirm';
  @Input() cancelLabel = 'Cancel';
  @Input() busy = false;
  @Input() danger = false;

  @Output() confirm = new EventEmitter<void>();
  @Output() cancel = new EventEmitter<void>();

  onBackdropClick(event: MouseEvent): void {
    if (this.busy) {
      return;
    }

    if (event.target === event.currentTarget) {
      this.cancel.emit();
    }
  }

  onCancel(): void {
    if (this.busy) {
      return;
    }

    this.cancel.emit();
  }

  onConfirm(): void {
    if (this.busy) {
      return;
    }

    this.confirm.emit();
  }
}
