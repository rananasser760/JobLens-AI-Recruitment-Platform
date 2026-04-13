import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { CandidateProfileDto } from '../../../../core/models/candidate.model';
import { ResumeDto } from '../../../../core/models/resume.model';
import { CandidateService } from '../../candidate.service';
import { ResumeService } from '../../../resumes/resume.service';
import { CandidateProfilePage } from './candidate-profile.page';

describe('CandidateProfilePage', () => {
  const profile: CandidateProfileDto = {
    id: 5,
    userId: 9,
    fullName: 'Candidate One',
    email: 'candidate@example.com',
    phone: '0101001001',
    location: 'Cairo',
    currentTitle: 'Frontend Developer',
    summary: 'Focused on Angular and performance optimization.',
    linkedInUrl: null,
    portfolioUrl: null,
    profileImagePath: null,
    yearsOfExperience: 4,
    createdAt: '2026-03-01T10:00:00Z',
    updatedAt: '2026-04-01T10:00:00Z',
    skills: [],
    resumes: []
  };

  const baseResume: ResumeDto = {
    id: 21,
    candidateId: 5,
    fileName: 'candidate-one.pdf',
    fileType: 'application/pdf',
    fileSize: 1024,
    resumeText: 'Sample resume text',
    isParsed: false,
    atsScore: 67,
    atsFriendly: false,
    atsRecommendations: null,
    isDefault: true,
    uploadedAt: '2026-04-02T09:00:00Z',
    parsingResult: null
  };

  const detailedResume: ResumeDto = {
    ...baseResume,
    isParsed: true,
    parsingResult: {
      id: 31,
      parsedJson: '{"skills":["Angular"]}',
      confidence: 0.94,
      summary: 'Strong frontend profile',
      extractedName: 'Candidate One',
      extractedEmail: 'candidate@example.com',
      extractedPhone: '0101001001',
      extractedSkills: ['Angular', 'TypeScript'],
      extractedExperience: [],
      extractedEducation: []
    }
  };

  let getResumeCalls = 0;
  let getStoredResumeScoreCalls = 0;

  let candidateServiceMock: Partial<CandidateService>;
  let resumeServiceMock: Partial<ResumeService>;

  beforeEach(async () => {
    getResumeCalls = 0;
    getStoredResumeScoreCalls = 0;

    candidateServiceMock = {
      getProfile: () => of({ success: true, message: 'ok', data: profile }),
      updateProfile: () => of({ success: true, message: 'updated', data: profile }),
      updateProfileImage: () =>
        of({ success: true, message: 'image uploaded', data: null }),
      addSkill: () => of({ success: true, message: 'skill added', data: null as never }),
      removeSkill: () => of({ success: true, message: 'skill removed', data: null }),
      fillFromResume: () => of({ success: true, message: 'filled', data: null })
    };

    resumeServiceMock = {
      getMyResumes: () => of({ success: true, message: 'ok', data: [baseResume] }),
      getResume: () => {
        getResumeCalls += 1;
        return of({ success: true, message: 'ok', data: detailedResume });
      },
      uploadResume: () => of({ success: true, message: 'uploaded', data: baseResume }),
      deleteResume: () => of({ success: true, message: 'deleted', data: null }),
      setDefaultResume: () => of({ success: true, message: 'default', data: null }),
      parseStoredResume: () => of({ success: true, message: 'parsed', data: null }),
      getStoredResumeAtsScore: () => {
        getStoredResumeScoreCalls += 1;
        return of({
          success: true,
          message: 'score',
          data: {
            resumeId: 21,
            score: 88,
            isFriendly: true,
            recommendations: ['Add measurable outcomes'],
            categoryScores: {
              Format: 90,
              Content: 86
            }
          }
        });
      },
      downloadResume: () => of(new Blob())
    };

    await TestBed.configureTestingModule({
      imports: [CandidateProfilePage],
      providers: [
        { provide: CandidateService, useValue: candidateServiceMock as unknown as CandidateService },
        { provide: ResumeService, useValue: resumeServiceMock as unknown as ResumeService }
      ]
    }).compileComponents();
  });

  it('loads profile and resume list on init', () => {
    const fixture = TestBed.createComponent(CandidateProfilePage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    expect(component.profile()?.id).toBe(5);
    expect(component.resumes().length).toBe(1);
    expect(component.resumes()[0].id).toBe(21);
  });

  it('fetches ATS report and updates displayed score', () => {
    const fixture = TestBed.createComponent(CandidateProfilePage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    component.fetchResumeScore(21);

    expect(getStoredResumeScoreCalls).toBe(1);
    expect(component.getDisplayedAtsScore(component.resumes()[0])).toBe(88);
  });

  it('loads detailed resume insights when inspecting a resume', () => {
    const fixture = TestBed.createComponent(CandidateProfilePage);
    fixture.detectChanges();

    const component = fixture.componentInstance;
    component.viewResumeInsights(21, true);

    expect(getResumeCalls).toBe(1);
    expect(component.selectedResumeId()).toBe(21);
    expect(component.selectedResume()?.parsingResult?.extractedSkills).toContain('Angular');
  });
});
