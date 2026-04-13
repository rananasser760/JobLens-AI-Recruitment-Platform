import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, finalize, forkJoin, map, of } from 'rxjs';

import { MetadataService } from '../../../../core/api/metadata.service';
import { EnumOptionDto } from '../../../../core/models/metadata.model';
import { EmploymentType, JobDto, UpdateJobDto } from '../../../../core/models/job.model';
import { JobsService } from '../../../jobs/jobs.service';

const FALLBACK_EMPLOYMENT_TYPES: EmploymentType[] = [
  'FullTime',
  'PartTime',
  'Contract',
  'Internship',
  'Freelance',
  'Remote'
];

@Component({
  selector: 'app-recruiter-job-detail-page',
  imports: [CommonModule, ReactiveFormsModule, RouterLink],
  templateUrl: './recruiter-job-detail.page.html',
  styleUrl: './recruiter-job-detail.page.css'
})
export class RecruiterJobDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);
  private readonly jobsService = inject(JobsService);
  private readonly metadataService = inject(MetadataService);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly toggling = signal(false);
  readonly addingSkill = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly jobId = signal<number | null>(null);
  readonly job = signal<JobDto | null>(null);
  readonly employmentTypes = signal<EmploymentType[]>([...FALLBACK_EMPLOYMENT_TYPES]);

  readonly newSkill = signal('');
  readonly newSkillImportance = signal('50');

  readonly form = this.fb.nonNullable.group({
    title: ['', [Validators.required, Validators.minLength(3)]],
    description: ['', [Validators.required, Validators.minLength(10)]],
    requirements: [''],
    responsibilities: [''],
    location: [''],
    employmentType: ['FullTime'],
    salaryRange: [''],
    salaryMin: [''],
    salaryMax: [''],
    experienceLevel: [''],
    expiresAt: [''],
    isActive: [true]
  });

  constructor() {
    this.route.paramMap.subscribe((params) => {
      const rawId = Number(params.get('jobId'));
      if (!Number.isFinite(rawId) || rawId <= 0) {
        this.loading.set(false);
        this.error.set('Invalid job identifier provided.');
        return;
      }

      this.jobId.set(rawId);
      this.load();
    });
  }

  load(): void {
    const id = this.jobId();
    if (!id) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    forkJoin({
      job: this.jobsService.getById(id).pipe(
        map((res) => res.data),
        catchError(() => of(null))
      ),
      employmentTypes: this.metadataService.getEnums().pipe(
        map((res) => this.extractEmploymentTypes(res.data?.enums?.['EmploymentType'] ?? [])),
        catchError(() => of([...FALLBACK_EMPLOYMENT_TYPES]))
      )
    })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: ({ job, employmentTypes }) => {
          this.employmentTypes.set(employmentTypes);
          this.job.set(job);

          if (!job) {
            this.error.set('Job details are unavailable.');
            return;
          }

          this.patchForm(job);
        },
        error: () => {
          this.error.set('Unable to load job details right now.');
        }
      });
  }

  save(): void {
    const id = this.jobId();
    if (!id || this.form.invalid || this.saving()) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.success.set(null);

    const raw = this.form.getRawValue();
    const payload: UpdateJobDto = {
      title: raw.title.trim() || undefined,
      description: raw.description.trim() || undefined,
      requirements: raw.requirements.trim() || undefined,
      responsibilities: raw.responsibilities.trim() || undefined,
      location: raw.location.trim() || undefined,
      employmentType: this.toEmploymentType(raw.employmentType) || undefined,
      salaryRange: raw.salaryRange.trim() || undefined,
      salaryMin: this.parseNumber(raw.salaryMin),
      salaryMax: this.parseNumber(raw.salaryMax),
      experienceLevel: raw.experienceLevel.trim() || undefined,
      expiresAt: this.toIsoDateTime(raw.expiresAt),
      isActive: raw.isActive
    };

    this.jobsService
      .update(id, payload)
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Job details updated successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to update job details right now.'));
        }
      });
  }

  toggleStatus(): void {
    const id = this.jobId();
    if (!id || this.toggling()) {
      return;
    }

    this.toggling.set(true);
    this.error.set(null);
    this.success.set(null);

    this.jobsService
      .toggleStatus(id)
      .pipe(finalize(() => this.toggling.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Job status toggled successfully.');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to toggle job status right now.'));
        }
      });
  }

  onNewSkillChanged(value: string): void {
    this.newSkill.set(value);
  }

  onNewSkillImportanceChanged(value: string): void {
    this.newSkillImportance.set(value);
  }

  addSkill(): void {
    const id = this.jobId();
    const skillName = this.newSkill().trim();

    if (!id || !skillName || this.addingSkill()) {
      return;
    }

    this.addingSkill.set(true);
    this.error.set(null);
    this.success.set(null);

    const parsedImportance = this.parseNumber(this.newSkillImportance());
    const importance = parsedImportance === undefined ? 50 : Math.max(1, Math.min(100, parsedImportance));

    this.jobsService
      .addSkill(id, {
        skillName,
        importance,
        isRequired: true
      })
      .pipe(finalize(() => this.addingSkill.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Skill added to job requirements.');
          this.newSkill.set('');
          this.newSkillImportance.set('50');
          this.load();
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to add this skill right now.'));
        }
      });
  }

  private patchForm(job: JobDto): void {
    this.form.patchValue({
      title: job.title || '',
      description: job.description || '',
      requirements: job.requirements || '',
      responsibilities: job.responsibilities || '',
      location: job.location || '',
      employmentType: this.toEmploymentType(job.employmentType) || 'FullTime',
      salaryRange: job.salaryRange || '',
      salaryMin: job.salaryMin?.toString() || '',
      salaryMax: job.salaryMax?.toString() || '',
      experienceLevel: job.experienceLevel || '',
      expiresAt: this.toDateTimeLocal(job.expiresAt),
      isActive: job.isActive
    });
  }

  private extractEmploymentTypes(options: EnumOptionDto[]): EmploymentType[] {
    const parsed = options
      .map((item) => item.name)
      .filter((name): name is EmploymentType => FALLBACK_EMPLOYMENT_TYPES.includes(name as EmploymentType));

    if (parsed.length === 0) {
      return [...FALLBACK_EMPLOYMENT_TYPES];
    }

    return parsed;
  }

  private toEmploymentType(value: string): EmploymentType | '' {
    if (FALLBACK_EMPLOYMENT_TYPES.includes(value as EmploymentType)) {
      return value as EmploymentType;
    }
    return '';
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

  private toDateTimeLocal(value?: string | null): string {
    if (!value) {
      return '';
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return '';
    }

    const offset = date.getTimezoneOffset();
    const local = new Date(date.getTime() - offset * 60_000);
    return local.toISOString().slice(0, 16);
  }

  private toIsoDateTime(value: unknown): string | undefined {
    if (value === null || value === undefined) {
      return undefined;
    }

    if (value instanceof Date) {
      return Number.isNaN(value.getTime()) ? undefined : value.toISOString();
    }

    if (typeof value !== 'string') {
      return undefined;
    }

    if (!value.trim()) {
      return undefined;
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return undefined;
    }

    return date.toISOString();
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
