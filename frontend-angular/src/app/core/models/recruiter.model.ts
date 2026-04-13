export interface CompanyBasicDto {
  id: number;
  name: string;
  website?: string | null;
  industry?: string | null;
  logoPath?: string | null;
}

export interface RecruiterProfileDto {
  id: number;
  userId: number;
  email: string;
  fullName?: string | null;
  phone?: string | null;
  position?: string | null;
  createdAt: string;
  company?: CompanyBasicDto | null;
}

export interface UpdateRecruiterProfileDto {
  fullName?: string;
  phone?: string;
  position?: string;
  companyId?: number;
}

export interface CompanyDto {
  id: number;
  name: string;
  website?: string | null;
  industry?: string | null;
  size?: number | null;
  logoPath?: string | null;
  description?: string | null;
  location?: string | null;
  createdAt: string;
  totalJobs: number;
  activeJobs: number;
}

export interface CreateCompanyDto {
  name: string;
  website?: string;
  industry?: string;
  size?: number;
  description?: string;
  location?: string;
}

export interface UpdateCompanyDto {
  name?: string;
  website?: string;
  industry?: string;
  size?: number;
  description?: string;
  location?: string;
}

export interface RecentApplicationDto {
  applicationId: number;
  candidateName: string;
  jobTitle: string;
  appliedAt: string;
  status: string;
}

export interface JobStatsDto {
  jobId: number;
  jobTitle: string;
  applicationCount: number;
  shortlistedCount: number;
}

export interface RecruiterDashboardDto {
  totalJobsPosted: number;
  activeJobs: number;
  totalApplications: number;
  pendingApplications: number;
  interviewsScheduled: number;
  interviewsCompleted: number;
  recentApplications: RecentApplicationDto[];
  topJobs: JobStatsDto[];
}
