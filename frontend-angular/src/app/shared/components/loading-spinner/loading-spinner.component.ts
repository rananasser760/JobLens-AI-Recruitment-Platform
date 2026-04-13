import { CommonModule } from '@angular/common';
import { Component, Input } from '@angular/core';

type SpinnerVariant = 'inline' | 'overlay';

@Component({
  selector: 'app-loading-spinner',
  imports: [CommonModule],
  templateUrl: './loading-spinner.component.html',
  styleUrl: './loading-spinner.component.css'
})
export class LoadingSpinnerComponent {
  @Input() active = false;
  @Input() variant: SpinnerVariant = 'inline';
  @Input() message = 'Loading...';
}