import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { errorMessage } from './util';

@Injectable({ providedIn: 'root' })
export class NotifyService {
  private readonly snack = inject(MatSnackBar);

  ok(message: string): void {
    this.snack.open(message, 'OK', { duration: 3000 });
  }

  error(err: unknown): void {
    this.snack.open(errorMessage(err), 'Close', { duration: 8000, panelClass: 'snack-error' });
  }
}
