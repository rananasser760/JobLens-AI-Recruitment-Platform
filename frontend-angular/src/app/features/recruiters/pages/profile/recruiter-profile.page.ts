import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { finalize } from 'rxjs';

import { CompanyService } from '../../company.service';
import { RecruiterService } from '../../recruiter.service';
import { CompanyDto, RecruiterProfileDto, UpdateRecruiterProfileDto } from '../../../../core/models/recruiter.model';

@Component({
  selector: 'app-recruiter-profile-page',
  imports: [CommonModule],
  templateUrl: './recruiter-profile.page.html',
  styleUrl: './recruiter-profile.page.css'
})
export class RecruiterProfilePage {
  private readonly recruiterService = inject(RecruiterService);
  private readonly companyService = inject(CompanyService);

  readonly loading = signal(true);
  readonly savingProfile = signal(false);
  readonly savingCompany = signal(false);
  readonly uploadingLogo = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly profile = signal<RecruiterProfileDto | null>(null);
  readonly companies = signal<CompanyDto[]>([]);
  readonly selectedCompany = signal<CompanyDto | null>(null);
  readonly selectedCompanyId = signal<number | null>(null);
  readonly selectedLogoFile = signal<File | null>(null);

  readonly fullName = signal('');
  readonly phone = signal('');
  readonly position = signal('');

  readonly companyName = signal('');
  readonly companyWebsite = signal('');
  readonly companyIndustry = signal('');
  readonly companySize = signal('');
  readonly companyLocation = signal('');
  readonly companyDescription = signal('');

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.recruiterService
      .getProfile()
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (res) => {
          const data = res.data;
          if (!data) {
            this.error.set('Recruiter profile data is unavailable.');
            return;
          }

          this.profile.set(data);
          this.fullName.set(data.fullName ?? '');
          this.phone.set(data.phone ?? '');
          this.position.set(data.position ?? '');

          const initialCompanyId = data.company?.id ?? null;
          this.selectedCompanyId.set(initialCompanyId);

          this.loadCompanies(initialCompanyId);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to load recruiter profile right now.'));
        }
      });
  }

  loadCompanies(preferredCompanyId: number | null): void {
    this.companyService.getAll().subscribe({
      next: (res) => {
        const all = res.data ?? [];
        this.companies.set(all);

        const chosenId = preferredCompanyId ?? this.selectedCompanyId();
        if (chosenId) {
          this.selectCompanyById(chosenId, all);
          return;
        }

        this.selectedCompany.set(null);
        this.resetCompanyForm();
      },
      error: () => {
        this.companies.set([]);
      }
    });
  }

  onFullNameChanged(value: string): void {
    this.fullName.set(value);
  }

  onPhoneChanged(value: string): void {
    this.phone.set(value);
  }

  onPositionChanged(value: string): void {
    this.position.set(value);
  }

  onCompanyNameChanged(value: string): void {
    this.companyName.set(value);
  }

  onCompanyWebsiteChanged(value: string): void {
    this.companyWebsite.set(value);
  }

  onCompanyIndustryChanged(value: string): void {
    this.companyIndustry.set(value);
  }

  onCompanySizeChanged(value: string): void {
    this.companySize.set(value);
  }

  onCompanyLocationChanged(value: string): void {
    this.companyLocation.set(value);
  }

  onCompanyDescriptionChanged(value: string): void {
    this.companyDescription.set(value);
  }

  onCompanySelectionChanged(value: string): void {
    const parsed = Number(value);
    const nextId = Number.isFinite(parsed) && parsed > 0 ? parsed : null;

    this.selectedCompanyId.set(nextId);

    if (!nextId) {
      this.selectedCompany.set(null);
      this.resetCompanyForm();
      return;
    }

    this.selectCompanyById(nextId, this.companies());
  }

  onLogoFileSelected(fileList: FileList | null): void {
    this.selectedLogoFile.set(fileList?.item(0) ?? null);
  }

  saveProfile(): void {
    if (this.savingProfile()) {
      return;
    }

    this.savingProfile.set(true);
    this.error.set(null);
    this.success.set(null);

    const payload: UpdateRecruiterProfileDto = {
      fullName: this.fullName().trim() || undefined,
      phone: this.phone().trim() || undefined,
      position: this.position().trim() || undefined,
      companyId: this.selectedCompanyId() ?? undefined
    };

    this.recruiterService
      .updateProfile(payload)
      .pipe(finalize(() => this.savingProfile.set(false)))
      .subscribe({
        next: (res) => {
          if (res.data) {
            this.profile.set(res.data);
          }
          this.success.set(res.message || 'Recruiter profile updated successfully.');
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to update recruiter profile right now.'));
        }
      });
  }

  saveCompany(): void {
    if (this.savingCompany()) {
      return;
    }

    if (!this.companyName().trim()) {
      this.error.set('Company name is required.');
      return;
    }

    this.savingCompany.set(true);
    this.error.set(null);
    this.success.set(null);

    const sizeValue = this.parseCompanySize(this.companySize());

    const selectedId = this.selectedCompanyId();
    if (selectedId) {
      this.companyService
        .update(selectedId, {
          name: this.companyName().trim(),
          website: this.companyWebsite().trim() || undefined,
          industry: this.companyIndustry().trim() || undefined,
          size: sizeValue,
          location: this.companyLocation().trim() || undefined,
          description: this.companyDescription().trim() || undefined
        })
        .pipe(finalize(() => this.savingCompany.set(false)))
        .subscribe({
          next: (res) => {
            if (res.data) {
              this.selectedCompany.set(res.data);
            }
            this.success.set(res.message || 'Company information updated successfully.');
            this.loadCompanies(selectedId);
          },
          error: (err: unknown) => {
            this.error.set(this.mapError(err, 'Unable to update company details right now.'));
          }
        });
      return;
    }

    this.companyService
      .create({
        name: this.companyName().trim(),
        website: this.companyWebsite().trim() || undefined,
        industry: this.companyIndustry().trim() || undefined,
        size: sizeValue,
        location: this.companyLocation().trim() || undefined,
        description: this.companyDescription().trim() || undefined
      })
      .pipe(finalize(() => this.savingCompany.set(false)))
      .subscribe({
        next: (res) => {
          const created = res.data;
          if (!created) {
            this.error.set('Company created but no company data was returned.');
            return;
          }

          this.selectedCompanyId.set(created.id);
          this.selectedCompany.set(created);

          this.recruiterService
            .updateProfile({
              fullName: this.fullName().trim() || undefined,
              phone: this.phone().trim() || undefined,
              position: this.position().trim() || undefined,
              companyId: created.id
            })
            .subscribe();

          this.success.set(res.message || 'Company created and linked to your profile.');
          this.loadCompanies(created.id);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to create company right now.'));
        }
      });
  }

  uploadLogo(): void {
    const companyId = this.selectedCompanyId();
    const file = this.selectedLogoFile();

    if (!companyId || !file || this.uploadingLogo()) {
      return;
    }

    this.uploadingLogo.set(true);
    this.error.set(null);
    this.success.set(null);

    this.companyService
      .updateLogo(companyId, file)
      .pipe(finalize(() => this.uploadingLogo.set(false)))
      .subscribe({
        next: (res) => {
          this.success.set(res.message || 'Company logo updated successfully.');
          this.selectedLogoFile.set(null);
          this.loadCompanies(companyId);
        },
        error: (err: unknown) => {
          this.error.set(this.mapError(err, 'Unable to upload company logo right now.'));
        }
      });
  }

  private selectCompanyById(companyId: number, companies: CompanyDto[]): void {
    const fromList = companies.find((item) => item.id === companyId) ?? null;
    if (fromList) {
      this.selectedCompany.set(fromList);
      this.patchCompanyForm(fromList);
      return;
    }

    this.companyService.getById(companyId).subscribe({
      next: (res) => {
        const company = res.data;
        this.selectedCompany.set(company ?? null);
        this.patchCompanyForm(company ?? null);
      },
      error: () => {
        this.selectedCompany.set(null);
        this.resetCompanyForm();
      }
    });
  }

  private patchCompanyForm(company: CompanyDto | null): void {
    if (!company) {
      this.resetCompanyForm();
      return;
    }

    this.companyName.set(company.name ?? '');
    this.companyWebsite.set(company.website ?? '');
    this.companyIndustry.set(company.industry ?? '');
    this.companySize.set(company.size !== null && company.size !== undefined ? String(company.size) : '');
    this.companyLocation.set(company.location ?? '');
    this.companyDescription.set(company.description ?? '');
  }

  private resetCompanyForm(): void {
    this.companyName.set('');
    this.companyWebsite.set('');
    this.companyIndustry.set('');
    this.companySize.set('');
    this.companyLocation.set('');
    this.companyDescription.set('');
    this.selectedLogoFile.set(null);
  }

  private parseCompanySize(value: string): number | undefined {
    if (!value.trim()) {
      return undefined;
    }

    const parsed = Number(value);
    if (!Number.isFinite(parsed) || parsed <= 0) {
      return undefined;
    }

    return Math.floor(parsed);
  }

  private mapError(err: unknown, fallback: string): string {
    if (err instanceof Error && err.message) {
      return err.message;
    }

    return fallback;
  }
}
