import { PaginationParams } from './api.model';

export type EmploymentType =
  | 'FullTime'
  | 'PartTime'
  | 'Contract'
  | 'Internship'
  | 'Freelance'
  | 'Remote';

export type JobSource = 'Internal' | 'Scraped';

export interface JobDto {
  id: number;
  title: string;
  description: string;
  requirements?: string | null;
  responsibilities?: string | null;
  location?: string | null;
  employmentType: string;
  salaryRange?: string | null;
  salaryMin?: number | null;
  salaryMax?: number | null;
  currency?: string | null;
  experienceLevel?: string | null;
  postedAt: string;
  expiresAt?: string | null;
  isActive: boolean;
  source: string;
  externalUrl?: string | null;
  companyName?: string | null;
  companyLogo?: string | null;
  requiredSkills: string[];
  applicationCount: number;
}

export interface JobListDto {
  id: number;
  title: string;
  location?: string | null;
  employmentType: string;
  salaryRange?: string | null;
  postedAt: string;
  companyName?: string | null;
  companyLogo?: string | null;
  source: string;
  topSkills: string[];
  matchScore?: number | null;
}

export interface CreateJobSkillDto {
  skillName: string;
  importance?: number;
  isRequired?: boolean;
}

export interface CreateJobDto {
  title: string;
  description: string;
  requirements?: string;
  responsibilities?: string;
  location?: string;
  employmentType: EmploymentType;
  salaryRange?: string;
  salaryMin?: number;
  salaryMax?: number;
  currency?: string;
  experienceLevel?: string;
  expiresAt?: string;
  requiredSkills: CreateJobSkillDto[];
}

export interface UpdateJobDto {
  title?: string;
  description?: string;
  requirements?: string;
  responsibilities?: string;
  location?: string;
  employmentType?: EmploymentType;
  salaryRange?: string;
  salaryMin?: number;
  salaryMax?: number;
  experienceLevel?: string;
  expiresAt?: string;
  isActive?: boolean;
}

export interface JobSearchParams extends PaginationParams {
  keyword?: string;
  location?: string;
  skills?: string;
  employmentType?: EmploymentType;
  experienceLevel?: string;
  minSalary?: number;
  maxSalary?: number;
  source?: JobSource;
  isActive?: boolean;
  companyId?: number;
}

export interface JobRecommendationDto {
  jobId: number;
  title: string;
  companyName?: string | null;
  location?: string | null;
  matchScore: number;
  matchingSkills: string[];
  matchReason?: string | null;
}

export interface ScrapedJobDto {
  externalJobId: string;
  title: string;
  description: string;
  requirements?: string | null;
  location?: string | null;
  salaryRange?: string | null;
  employmentType?: string | null;
  externalUrl: string;
  externalSource: string;
  companyName?: string | null;
  postedAt: string;
  skills?: string[] | null;
}

export interface MatchJobsFromTextRequestDto {
  resumeText: string;
}

export interface ScrapingStatusDto {
  isRunning?: boolean;
  status?: string;
  queuedCount?: number;
  lastRunAtUtc?: string | null;
  nextRunAtUtc?: string | null;
  [key: string]: unknown;
}

export interface RecruitmentStatusDto {
  status?: string;
  phase?: string;
  updatedAtUtc?: string | null;
  [key: string]: unknown;
}

export interface JobOperationResultDto {
  queued?: boolean;
  backgroundJobId?: number | null;
  status?: string;
  message?: string;
  [key: string]: unknown;
}
