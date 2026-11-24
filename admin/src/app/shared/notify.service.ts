import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { I18nService } from '../core/i18n.service';
import { errorMessage } from './util';

@Injectable({ providedIn: 'root' })
export class NotifyService {
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);

  ok(message: string): void {
    this.snack.open(message, this.i18n.t('OK'), { duration: 3000 });
  }

  error(err: unknown): void {
    this.snack.open(this.i18n.t(errorMessage(err)), this.i18n.t('Close'), { duration: 8000, panelClass: 'snack-error' });
  }
}
