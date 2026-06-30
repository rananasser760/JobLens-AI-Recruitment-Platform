import { PaginationParams } from './api.model';

export interface CandidateSkillDto {
  id: number;
  skillName: string;
  experienceYears?: number | null;
  skillConfidence?: number | null;
  proficiencyLevel?: string | null;
  isVerified: boolean;
}

export interface ResumeBasicDto {
  id: number;
  fileName: string;
  fileType?: string | null;
  isParsed: boolean;
  atsScore?: number | null;
  isDefault: boolean;
  uploadedAt: string;
}

export interface CandidateProfileDto {
  id: number;
  userId: number;
  fullName?: string | null;
  email: string;
  phone?: string | null;
  location?: string | null;
  currentTitle?: string | null;
  summary?: string | null;
  linkedInUrl?: string | null;
  portfolioUrl?: string | null;
  profileImagePath?: string | null;
  yearsOfExperience?: number | null;
  createdAt: string;
  updatedAt: string;
  skills: CandidateSkillDto[];
  resumes: ResumeBasicDto[];
}

export interface UpdateCandidateProfileDto {
  fullName?: string;
  phone?: string;
  location?: string;
  currentTitle?: string;
  summary?: string;
  linkedInUrl?: string;
  portfolioUrl?: string;
  yearsOfExperience?: number;
}

export interface AddSkillDto {
  skillName: string;
  experienceYears?: number;
  proficiencyLevel?: string;
}

export interface FillProfileFromResumeResultDto {
  updatedFields?: string[];
  profile?: CandidateProfileDto | null;
  [key: string]: unknown;
}

export interface CandidateListDto {
  id: number;
  fullName?: string | null;
  email: string;
  currentTitle?: string | null;
  location?: string | null;
  yearsOfExperience?: number | null;
  profileImagePath?: string | null;
  topSkills: string[];
}

export interface CandidateSearchParams extends PaginationParams {
  keyword?: string;
  location?: string;
  skills?: string;
  minExperience?: number;
  maxExperience?: number;
}

export interface CandidateRecentApplicationDto {
  applicationId: number;
  jobId: number;
  jobTitle: string;
  companyName?: string | null;
  appliedAt: string;
  status: string;
  atsScore?: number | null;
}

export interface CandidateUpcomingInterviewDto {
  sessionId: number;
  applicationId: number;
  jobTitle: string;
  interviewTitle?: string | null;
  scheduledAt?: string | null;
  status: string;
  cheatingDetected: boolean;
  overallScore?: number | null;
}

export interface CandidateDashboardDto {
  totalApplications: number;
  activeApplications: number;
  interviewsScheduled: number;
  interviewsCompleted: number;
  highestAtsScore: number;
  recentApplications: CandidateRecentApplicationDto[];
  upcomingInterviews: CandidateUpcomingInterviewDto[];
}
