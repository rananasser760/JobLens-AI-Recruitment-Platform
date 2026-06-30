export interface ExperienceDto {
  title?: string | null;
  company?: string | null;
  duration?: string | null;
  description?: string | null;
}

export interface EducationDto {
  degree?: string | null;
  institution?: string | null;
  year?: string | null;
  field?: string | null;
}

export interface ResumeParsingResultDto {
  id: number;
  parsedJson: string;
  confidence?: number | null;
  summary?: string | null;
  extractedName?: string | null;
  extractedEmail?: string | null;
  extractedPhone?: string | null;
  extractedSkills: string[];
  extractedExperience: ExperienceDto[];
  extractedEducation: EducationDto[];
}

export interface ResumeDto {
  id: number;
  candidateId: number;
  fileName: string;
  fileType?: string | null;
  fileSize?: number | null;
  resumeText?: string | null;
  isParsed: boolean;
  atsScore?: number | null;
  atsFriendly: boolean;
  atsRecommendations?: string | null;
  isDefault: boolean;
  uploadedAt: string;
  parsingResult?: ResumeParsingResultDto | null;
}

export interface ResumeTextRequestDto {
  resumeText: string;
}

export interface ResumeTextAtsRequestDto extends ResumeTextRequestDto {
  jobDescription?: string;
}

export interface ResumeFullAnalysisRequestDto extends ResumeTextRequestDto {
  includeImprovements?: boolean;
  jobMatchLimit?: number;
}

export interface ParsedExperienceDto {
  jobTitle?: string | null;
  company?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  description?: string | null;
}

export interface ParsedEducationDto {
  degree?: string | null;
  institution?: string | null;
  graduationYear?: string | null;
  fieldOfStudy?: string | null;
}

export interface ParsedCvResponseDto {
  fullName?: string | null;
  email?: string | null;
  phone?: string | null;
  location?: string | null;
  summary?: string | null;
  skills?: string[];
  experience?: ParsedExperienceDto[];
  education?: ParsedEducationDto[];
  confidence: number;
}

export interface AtsScoreDto {
  resumeId: number;
  score: number;
  isFriendly: boolean;
  recommendations: string[];
  categoryScores: Record<string, number>;
}

export interface ResumeImprovementDto {
  section?: string | null;
  suggestion: string;
  reason?: string | null;
  priority?: 'Low' | 'Medium' | 'High' | string;
}

export interface ResumeImprovementsResponseDto {
  summary?: string | null;
  improvements: ResumeImprovementDto[];
}

export interface ResumeFullAnalysisResponseDto {
  parsedCv?: ParsedCvResponseDto | null;
  atsScore?: AtsScoreDto | null;
  improvements?: ResumeImprovementDto[];
  recommendations?: string[];
  [key: string]: unknown;
}
