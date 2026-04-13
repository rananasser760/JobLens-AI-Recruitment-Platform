import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { catchError, finalize, map, of } from 'rxjs';

import { MetadataService } from '../../../../core/api/metadata.service';
import { EnumOptionDto } from '../../../../core/models/metadata.model';
import { CreateJobDto, CreateJobSkillDto, EmploymentType } from '../../../../core/models/job.model';
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
  selector: 'app-recruiter-job-create-page',
  imports: [CommonModule, ReactiveFormsModule, RouterLink],
  templateUrl: './recruiter-job-create.page.html',
  styleUrl: './recruiter-job-create.page.css'
})
export class RecruiterJobCreatePage {
  private readonly fb = inject(FormBuilder);
  private readonly router = inject(Router);
  private readonly jobsService = inject(JobsService);
  private readonly metadataService = inject(MetadataService);

  readonly loadingMetadata = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly employmentTypes = signal<EmploymentType[]>([...FALLBACK_EMPLOYMENT_TYPES]);
  readonly requiredSkills = signal<CreateJobSkillDto[]>([]);

  readonly newSkillName = signal('');
  readonly newSkillImportance = signal('50');
  readonly newSkillRequired = signal(true);

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
    currency: ['USD'],
    experienceLevel: [''],
    expiresAt: ['']
  });

  constructor() {
    this.loadMetadata();
  }

  loadMetadata(): void {
    this.loadingMetadata.set(true);

    this.metadataService
      .getEnums()
      .pipe(
        map((res) => this.extractEmploymentTypes(res.data?.enums?.['EmploymentType'] ?? [])),
        catchError(() => of([...FALLBACK_EMPLOYMENT_TYPES])),
        finalize(() => this.loadingMetadata.set(false))
      )
      .subscribe({
        next: (types) => this.employmentTypes.set(types)
      });
  }

  onNewSkillNameChanged(value: string): void {
    this.newSkillName.set(value);
  }

  onNewSkillImportanceChanged(value: string): void {
    this.newSkillImportance.set(value);
  }

  onNewSkillRequiredChanged(value: boolean): void {
    this.newSkillRequired.set(value);
  }

  addSkill(): void {
    const skillName = this.newSkillName().trim();
    if (!skillName) {
      return;
    }

    const duplicate = this.requiredSkills().some(
      (item) => item.skillName.toLowerCase() === skillName.toLowerCase()
    );

    if (duplicate) {
      this.error.set(`Skill "${skillName}" already exists in this job.`);
      return;
    }

    const parsedImportance = this.parseNumber(this.newSkillImportance());
    const importance = parsedImportance === undefined ? 50 : Math.max(1, Math.min(100, parsedImportance));

    this.requiredSkills.set([
      ...this.requiredSkills(),
      {
        skillName,
        importance,
        isRequired: this.newSkillRequired()
      }
    ]);

    this.error.set(null);
    this.newSkillName.set('');
    this.newSkillImportance.set('50');
    this.newSkillRequired.set(true);
  }

  removeSkill(index: number): void {
    const next = [...this.requiredSkills()];
    if (index < 0 || index >= next.length) {
      return;
    }

    next.splice(index, 1);
    this.requiredSkills.set(next);
  }

  save(): void {
    if (this.form.invalid || this.saving()) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.success.set(null);

    const raw = this.form.getRawValue();
    const payload: CreateJobDto = {
      title: raw.title.trim(),
      description: raw.description.trim(),
      requirements: raw.requirements.trim() || undefined,
      responsibilities: raw.responsibilities.trim() || undefined,
      location: raw.location.trim() || undefined,
      employmentType: this.toEmploymentType(raw.employmentType) || 'FullTime',
      salaryRange: raw.salaryRange.trim() || undefined,
      salaryMin: this.parseNumber(raw.salaryMin),
      salaryMax: this.parseNumber(raw.salaryMax),
      currency: raw.currency.trim() || undefined,
      experienceLevel: raw.experienceLevel.trim() || undefined,
      expiresAt: this.toIsoDateTime(raw.expiresAt),
      requiredSkills: [...this.requiredSkills()]
    };

    this.jobsService
      .create(payload)
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: (res) => {
          const jobId = res.data?.id;
          this.success.set(res.message || 'Job created successfully.');

          if (jobId) {
            void this.router.navigate(['/recruiter/jobs', jobId]);
            return;
          }

          void this.router.navigate(['/recruiter/jobs']);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to create this job right now.'));
        }
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

    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) {
      return undefined;
    }

    return parsed.toISOString();
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
