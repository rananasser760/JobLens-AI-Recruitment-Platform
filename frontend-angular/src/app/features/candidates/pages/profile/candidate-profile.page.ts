import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import {
  AddSkillDto,
  CandidateProfileDto,
  ResumeBasicDto,
  UpdateCandidateProfileDto
} from '../../../../core/models/candidate.model';
import { AtsScoreDto, ResumeDto } from '../../../../core/models/resume.model';
import { CandidateService } from '../../candidate.service';
import { ResumeService } from '../../../resumes/resume.service';
import { ConfirmDialogComponent } from '../../../../shared/components/confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-candidate-profile-page',
  imports: [CommonModule, ReactiveFormsModule, ConfirmDialogComponent],
  templateUrl: './candidate-profile.page.html',
  styleUrl: './candidate-profile.page.css'
})
export class CandidateProfilePage {
  private readonly fb = inject(FormBuilder);
  private readonly candidateService = inject(CandidateService);
  private readonly resumeService = inject(ResumeService);

  readonly loading = signal(true);
  readonly savingProfile = signal(false);
  readonly uploadingImage = signal(false);
  readonly submittingSkill = signal(false);
  readonly uploadingResume = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly profile = signal<CandidateProfileDto | null>(null);
  readonly resumes = signal<ResumeDto[]>([]);
  readonly resumeDetails = signal<Record<number, ResumeDto>>({});
  readonly atsReports = signal<Record<number, AtsScoreDto>>({});
  readonly selectedResumeId = signal<number | null>(null);
  readonly resumeBusy = signal<Record<number, boolean>>({});
  readonly skillRemoving = signal<Record<number, boolean>>({});

  readonly selectedResume = computed(() => {
    const resumeId = this.selectedResumeId();
    if (!resumeId) {
      return null;
    }

    const fromDetails = this.resumeDetails()[resumeId];
    if (fromDetails) {
      return fromDetails;
    }

    return this.resumes().find((item) => item.id === resumeId) ?? null;
  });

  readonly selectedAtsReport = computed(() => {
    const selected = this.selectedResume();
    if (!selected) {
      return null;
    }

    return this.atsReports()[selected.id] ?? null;
  });

  readonly selectedAtsRows = computed(() => {
    const report = this.selectedAtsReport();
    const rows = Object.entries(report?.categoryScores ?? {});
    rows.sort((left, right) => right[1] - left[1]);
    return rows;
  });
  readonly pendingDeleteBusy = computed(() => {
    const pending = this.pendingResumeDelete();
    return pending ? this.isResumeBusy(pending.id) : false;
  });

  readonly selectedImageFile = signal<File | null>(null);
  readonly selectedResumeFile = signal<File | null>(null);
  readonly uploadAsDefault = signal(false);
  readonly uploadParseNow = signal(true);
  readonly confirmDeleteResumeOpen = signal(false);
  readonly pendingResumeDelete = signal<ResumeDto | null>(null);

  readonly profileForm = this.fb.nonNullable.group({
    fullName: [''],
    phone: [''],
    location: [''],
    currentTitle: [''],
    summary: [''],
    linkedInUrl: [''],
    portfolioUrl: [''],
    yearsOfExperience: ['']
  });

  readonly skillForm = this.fb.nonNullable.group({
    skillName: ['', [Validators.required, Validators.minLength(2)]],
    experienceYears: [''],
    proficiencyLevel: ['']
  });

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      profile: this.candidateService.getProfile().pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      resumes: this.resumeService.getMyResumes().pipe(
        map((res) => res.data ?? []),
        catchError(() => of([] as ResumeDto[]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ profile, resumes }) => {
          this.profile.set(profile);

          if (!profile) {
            this.error.set('Candidate profile is unavailable right now.');
            return;
          }

          this.patchProfileForm(profile);
          const mergedResumes =
            resumes.length > 0 ? resumes : this.mapProfileResumes(profile, profile.resumes)
          this.resumes.set(mergedResumes);
          this.cacheResumeDetails(mergedResumes);

          const selectedId = this.selectedResumeId();
          if (selectedId && !mergedResumes.some((resume) => resume.id === selectedId)) {
            this.selectedResumeId.set(null);
          }
        },
        error: () => {
          this.error.set('Unable to load candidate settings right now.');
        }
      });
  }

  saveProfile(): void {
    if (this.savingProfile()) {
      return;
    }

    this.savingProfile.set(true);
    this.error.set(null);
    this.success.set(null);

    const raw = this.profileForm.getRawValue();
    const payload: UpdateCandidateProfileDto = {
      fullName: raw.fullName.trim() || undefined,
      phone: raw.phone.trim() || undefined,
      location: raw.location.trim() || undefined,
      currentTitle: raw.currentTitle.trim() || undefined,
      summary: raw.summary.trim() || undefined,
      linkedInUrl: raw.linkedInUrl.trim() || undefined,
      portfolioUrl: raw.portfolioUrl.trim() || undefined,
      yearsOfExperience: this.parseNumber(raw.yearsOfExperience)
    };

    this.candidateService
      .updateProfile(payload)
      .pipe(finalize(() => this.savingProfile.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Profile details updated successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to update profile details right now.'));
        }
      });
  }

  onProfileImageSelected(fileList: FileList | null): void {
    this.selectedImageFile.set(fileList?.item(0) ?? null);
  }

  uploadProfileImage(): void {
    const file = this.selectedImageFile();
    if (!file || this.uploadingImage()) {
      return;
    }

    this.uploadingImage.set(true);
    this.error.set(null);
    this.success.set(null);

    this.candidateService
      .updateProfileImage(file)
      .pipe(finalize(() => this.uploadingImage.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Profile image uploaded successfully.');
          this.selectedImageFile.set(null);
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to upload profile image right now.'));
        }
      });
  }

  addSkill(): void {
    if (this.skillForm.invalid || this.submittingSkill()) {
      this.skillForm.markAllAsTouched();
      return;
    }

    this.submittingSkill.set(true);
    this.error.set(null);
    this.success.set(null);

    const raw = this.skillForm.getRawValue();
    const payload: AddSkillDto = {
      skillName: raw.skillName.trim(),
      experienceYears: this.parseNumber(raw.experienceYears),
      proficiencyLevel: raw.proficiencyLevel.trim() || undefined
    };

    this.candidateService
      .addSkill(payload)
      .pipe(finalize(() => this.submittingSkill.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Skill added successfully.');
          this.skillForm.reset({
            skillName: '',
            experienceYears: '',
            proficiencyLevel: ''
          });
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to add this skill right now.'));
        }
      });
  }

  isSkillRemoving(skillId: number): boolean {
    return !!this.skillRemoving()[skillId];
  }

  removeSkill(skillId: number): void {
    if (this.isSkillRemoving(skillId)) {
      return;
    }

    this.setSkillRemoving(skillId, true);
    this.error.set(null);
    this.success.set(null);

    this.candidateService
      .removeSkill(skillId)
      .pipe(finalize(() => this.setSkillRemoving(skillId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Skill removed successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to remove this skill right now.'));
        }
      });
  }

  onResumeFileSelected(fileList: FileList | null): void {
    this.selectedResumeFile.set(fileList?.item(0) ?? null);
  }

  onUploadAsDefaultChanged(value: boolean): void {
    this.uploadAsDefault.set(value);
  }

  onUploadParseNowChanged(value: boolean): void {
    this.uploadParseNow.set(value);
  }

  uploadResume(): void {
    const file = this.selectedResumeFile();
    if (!file || this.uploadingResume()) {
      return;
    }

    this.uploadingResume.set(true);
    this.error.set(null);
    this.success.set(null);

    this.resumeService
      .uploadResume(file, this.uploadAsDefault(), this.uploadParseNow())
      .pipe(finalize(() => this.uploadingResume.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Resume uploaded successfully.');
          this.selectedResumeFile.set(null);
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to upload this resume right now.'));
        }
      });
  }

  isResumeBusy(resumeId: number): boolean {
    return !!this.resumeBusy()[resumeId];
  }

  setDefaultResume(resumeId: number): void {
    if (this.isResumeBusy(resumeId)) {
      return;
    }

    this.setResumeBusy(resumeId, true);
    this.error.set(null);
    this.success.set(null);

    this.resumeService
      .setDefaultResume(resumeId)
      .pipe(finalize(() => this.setResumeBusy(resumeId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Default resume updated.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to change default resume right now.'));
        }
      });
  }

  parseResume(resumeId: number): void {
    if (this.isResumeBusy(resumeId)) {
      return;
    }

    this.setResumeBusy(resumeId, true);
    this.error.set(null);
    this.success.set(null);

    this.resumeService
      .parseStoredResume(resumeId)
      .pipe(finalize(() => this.setResumeBusy(resumeId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Resume parsing request completed.');
          this.viewResumeInsights(resumeId, true);
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to parse this resume right now.'));
        }
      });
  }

  fetchResumeScore(resumeId: number): void {
    if (this.isResumeBusy(resumeId)) {
      return;
    }

    this.setResumeBusy(resumeId, true);
    this.error.set(null);
    this.success.set(null);

    this.resumeService
      .getStoredResumeAtsScore(resumeId)
      .pipe(finalize(() => this.setResumeBusy(resumeId, false)))
      .subscribe({
        next: (res) => {
          if (res.data) {
            this.atsReports.set({
              ...this.atsReports(),
              [resumeId]: res.data
            });
            this.updateResumeInList(resumeId, (resume) => ({
              ...resume,
              atsScore: res.data?.score ?? resume.atsScore
            }));
          }
          this.success.set(res.message || 'ATS score refreshed.');
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to fetch ATS score for this resume right now.'));
        }
      });
  }

  fillProfileFromResume(resumeId: number): void {
    if (this.isResumeBusy(resumeId)) {
      return;
    }

    this.setResumeBusy(resumeId, true);
    this.error.set(null);
    this.success.set(null);

    this.candidateService
      .fillFromResume(resumeId)
      .pipe(finalize(() => this.setResumeBusy(resumeId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Profile was updated from selected resume.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to update profile from this resume right now.'));
        }
      });
  }

  downloadResume(resume: ResumeDto): void {
    const resumeId = resume.id;
    if (this.isResumeBusy(resumeId)) {
      return;
    }

    this.setResumeBusy(resumeId, true);
    this.error.set(null);
    this.success.set(null);

    this.resumeService
      .downloadResume(resumeId)
      .pipe(finalize(() => this.setResumeBusy(resumeId, false)))
      .subscribe({
        next: (blob) => {
          this.triggerResumeDownload(blob, resume.fileName || `resume-${resumeId}.pdf`);
          this.success.set('Resume downloaded successfully.');
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to download this resume right now.'));
        }
      });
  }

  requestDeleteResume(resume: ResumeDto): void {
    if (this.isResumeBusy(resume.id)) {
      return;
    }

    this.pendingResumeDelete.set(resume);
    this.confirmDeleteResumeOpen.set(true);
  }

  cancelDeleteResume(): void {
    const pending = this.pendingResumeDelete();
    if (pending && this.isResumeBusy(pending.id)) {
      return;
    }

    this.pendingResumeDelete.set(null);
    this.confirmDeleteResumeOpen.set(false);
  }

  confirmDeleteResume(): void {
    const resume = this.pendingResumeDelete();
    if (!resume || this.isResumeBusy(resume.id)) {
      this.cancelDeleteResume();
      return;
    }

    const resumeId = resume.id;
    this.confirmDeleteResumeOpen.set(false);
    this.pendingResumeDelete.set(null);

    this.setResumeBusy(resumeId, true);
    this.error.set(null);
    this.success.set(null);

    this.resumeService
      .deleteResume(resumeId)
      .pipe(finalize(() => this.setResumeBusy(resumeId, false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Resume deleted successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to delete this resume right now.'));
        }
      });
  }

  getDisplayedAtsScore(resume: ResumeDto): number | null {
    const reportScore = this.atsReports()[resume.id]?.score;
    if (reportScore !== undefined) {
      return reportScore;
    }

    return resume.atsScore ?? null;
  }

  viewResumeInsights(resumeId: number, force = false): void {
    this.selectedResumeId.set(resumeId);
    this.loadResumeDetails(resumeId, force);
  }

  closeResumeInsights(): void {
    this.selectedResumeId.set(null);
  }

  isResumeSelected(resumeId: number): boolean {
    return this.selectedResumeId() === resumeId;
  }

  private patchProfileForm(profile: CandidateProfileDto): void {
    this.profileForm.patchValue({
      fullName: profile.fullName || '',
      phone: profile.phone || '',
      location: profile.location || '',
      currentTitle: profile.currentTitle || '',
      summary: profile.summary || '',
      linkedInUrl: profile.linkedInUrl || '',
      portfolioUrl: profile.portfolioUrl || '',
      yearsOfExperience:
        profile.yearsOfExperience !== null && profile.yearsOfExperience !== undefined
          ? String(profile.yearsOfExperience)
          : ''
    });
  }

  private mapProfileResumes(profile: CandidateProfileDto, resumes: ResumeBasicDto[]): ResumeDto[] {
    return resumes.map((resume) => ({
      id: resume.id,
      candidateId: profile.id,
      fileName: resume.fileName,
      fileType: resume.fileType ?? null,
      fileSize: null,
      resumeText: null,
      isParsed: resume.isParsed,
      atsScore: resume.atsScore ?? null,
      atsFriendly: false,
      atsRecommendations: null,
      isDefault: resume.isDefault,
      uploadedAt: resume.uploadedAt,
      parsingResult: null
    }));
  }

  private loadResumeDetails(resumeId: number, force: boolean): void {
    if (!force && this.resumeDetails()[resumeId]) {
      return;
    }

    this.setResumeBusy(resumeId, true);

    this.resumeService
      .getResume(resumeId)
      .pipe(finalize(() => this.setResumeBusy(resumeId, false)))
      .subscribe({
        next: (res) => {
          const detailedResume = res.data;
          if (!detailedResume) {
            return;
          }

          this.resumeDetails.set({
            ...this.resumeDetails(),
            [resumeId]: detailedResume
          });
          this.updateResumeInList(resumeId, () => detailedResume);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load resume details right now.'));
        }
      });
  }

  private cacheResumeDetails(resumes: ResumeDto[]): void {
    const next = { ...this.resumeDetails() };
    for (const resume of resumes) {
      next[resume.id] = resume;
    }
    this.resumeDetails.set(next);
  }

  private updateResumeInList(resumeId: number, updater: (resume: ResumeDto) => ResumeDto): void {
    const current = this.resumes();
    const index = current.findIndex((resume) => resume.id === resumeId);
    if (index < 0) {
      return;
    }

    const next = [...current];
    next[index] = updater(next[index]);
    this.resumes.set(next);
  }

  private parseNumber(value: unknown): number | undefined {
    if (value === null || value === undefined) {
      return undefined;
    }

    if (typeof value === 'number') {
      return Number.isFinite(value) ? value : undefined;
    }

    if (typeof value !== 'string') {
      return undefined;
    }

    if (!value.trim()) {
      return undefined;
    }

    const parsed = Number(value);
    if (!Number.isFinite(parsed)) {
      return undefined;
    }

    return parsed;
  }

  private setResumeBusy(resumeId: number, value: boolean): void {
    const next = { ...this.resumeBusy() };
    if (value) {
      next[resumeId] = true;
    } else {
      delete next[resumeId];
    }
    this.resumeBusy.set(next);
  }

  private setSkillRemoving(skillId: number, value: boolean): void {
    const next = { ...this.skillRemoving() };
    if (value) {
      next[skillId] = true;
    } else {
      delete next[skillId];
    }
    this.skillRemoving.set(next);
  }

  private triggerResumeDownload(blob: Blob, fileName: string): void {
    if (typeof window === 'undefined' || typeof document === 'undefined') {
      return;
    }

    const objectUrl = window.URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = fileName;
    anchor.click();
    window.URL.revokeObjectURL(objectUrl);
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
