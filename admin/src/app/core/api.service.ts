import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Campaign,
  CampaignStats,
  ExplainResponse,
  PlatformConfig,
  RegisterVideoRequest,
  VideoJob,
} from './models';

/** Thin typed client over the advideo management API (same origin, key added by the interceptor). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // platforms
  platforms(): Observable<PlatformConfig[]> {
    return this.http.get<PlatformConfig[]>('/platforms');
  }
  platform(id: string): Observable<PlatformConfig> {
    return this.http.get<PlatformConfig>(`/platforms/${enc(id)}`);
  }
  createPlatform(p: PlatformConfig): Observable<PlatformConfig> {
    return this.http.post<PlatformConfig>('/platforms', p);
  }
  updatePlatform(p: PlatformConfig): Observable<PlatformConfig> {
    return this.http.put<PlatformConfig>(`/platforms/${enc(p.id)}`, p);
  }
  deletePlatform(id: string): Observable<void> {
    return this.http.delete<void>(`/platforms/${enc(id)}`);
  }

  // campaigns
  campaigns(): Observable<Campaign[]> {
    return this.http.get<Campaign[]>('/campaigns');
  }
  campaign(id: string): Observable<Campaign> {
    return this.http.get<Campaign>(`/campaigns/${enc(id)}`);
  }
  createCampaign(c: Campaign): Observable<Campaign> {
    return this.http.post<Campaign>('/campaigns', c);
  }
  updateCampaign(c: Campaign): Observable<Campaign> {
    return this.http.put<Campaign>(`/campaigns/${enc(c.id)}`, c);
  }
  deleteCampaign(id: string): Observable<void> {
    return this.http.delete<void>(`/campaigns/${enc(id)}`);
  }
  pauseCampaign(id: string): Observable<Campaign> {
    return this.http.post<Campaign>(`/campaigns/${enc(id)}/pause`, null);
  }
  resumeCampaign(id: string): Observable<Campaign> {
    return this.http.post<Campaign>(`/campaigns/${enc(id)}/resume`, null);
  }
  campaignStats(id: string): Observable<CampaignStats> {
    return this.http.get<CampaignStats>(`/campaigns/${enc(id)}/stats`);
  }

  // videos (analysis jobs)
  videos(): Observable<VideoJob[]> {
    return this.http.get<VideoJob[]>('/videos');
  }
  registerVideo(req: RegisterVideoRequest): Observable<VideoJob> {
    return this.http.post<VideoJob>('/videos', req);
  }
  videoMap(videoId: string): Observable<unknown> {
    return this.http.get<unknown>(`/videos/${enc(videoId)}/map`);
  }

  // ads
  explain(platform: string, videoId: string, t: number): Observable<ExplainResponse> {
    const params = new HttpParams().set('platform', platform).set('videoId', videoId).set('t', String(t));
    return this.http.get<ExplainResponse>('/ads/explain', { params });
  }
}

function enc(id: string): string {
  return encodeURIComponent(id);
}
