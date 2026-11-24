/**
 * Translation keys used only through variables (enum values, computed texts, API error fallbacks, route titles).
 * Listed here so `npm run i18n:extract` puts them into public/i18n/en.json; templates/TS literals are found
 * automatically ('Text' | t, i18n.t('Text'), t('Text')).
 */
export const DYNAMIC_KEYS = [
  // route titles
  'Overview', 'Campaigns', 'Campaign', 'Platforms', 'Platform', 'Videos', 'Explain',
  // campaign statuses (API values)
  'draft', 'active', 'paused', 'ended',
  // video job statuses (API values)
  'Queued', 'Running', 'Retrying', 'Completed', 'Failed', 'Failed (dead letter)',
  // buttons with computed labels
  'Create', 'Save',
  // brand-safety preset hints (platform edit)
  'No ads in sensitive content, profanity, drug references or war/military news (also without violence).',
  'Sensitive segments may carry ads when they are a news report; otherwise like Standard.',
  'No ads in sensitive content (violence, death, war, disasters, accidents, weapons, protests).',
  // shared/util errorMessage fallbacks
  'API is not reachable.',
  'Not allowed for this API key (admin role required).',
  'Invalid API key.',
  // snack bar actions
  'OK', 'Close',
] as const;
