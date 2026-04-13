import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';
import { interviewSessionLeaveGuard } from './core/guards/interview-session.guard';
import { roleGuard } from './core/guards/role.guard';
import { AuthLayoutComponent } from './layouts/auth-layout/auth-layout.component';
import { CandidateLayoutComponent } from './layouts/candidate-layout/candidate-layout.component';
import { RecruiterLayoutComponent } from './layouts/recruiter-layout/recruiter-layout.component';

export const routes: Routes = [
	{
		path: '',
		pathMatch: 'full',
		redirectTo: 'auth/login'
	},
	{
		path: 'auth',
		component: AuthLayoutComponent,
		children: [
			{
				path: '',
				pathMatch: 'full',
				redirectTo: 'login'
			},
			{
				path: 'login',
				loadComponent: () =>
					import('./features/auth/pages/login/login.page').then((m) => m.LoginPage)
			},
			{
				path: 'register',
				loadComponent: () =>
					import('./features/auth/pages/register/register.page').then((m) => m.RegisterPage)
			},
			{
				path: 'forgot-password',
				loadComponent: () =>
					import('./features/auth/pages/forgot-password/forgot-password.page').then(
						(m) => m.ForgotPasswordPage
					)
			},
			{
				path: 'reset-password',
				loadComponent: () =>
					import('./features/auth/pages/reset-password/reset-password.page').then(
						(m) => m.ResetPasswordPage
					)
			}
		]
	},
	{
		path: 'candidate',
		component: CandidateLayoutComponent,
		canActivate: [authGuard, roleGuard(['Candidate'])],
		children: [
			{
				path: '',
				pathMatch: 'full',
				redirectTo: 'dashboard'
			},
			{
				path: 'dashboard',
				loadComponent: () =>
					import('./features/candidates/pages/dashboard/candidate-dashboard.page').then(
						(m) => m.CandidateDashboardPage
					)
			},
			{
				path: 'recommendations',
				loadComponent: () =>
					import('./features/candidates/pages/recommendations/candidate-recommendations.page').then(
						(m) => m.CandidateRecommendationsPage
					)
			},
			{
				path: 'applications',
				loadComponent: () =>
					import('./features/candidates/pages/applications/candidate-applications.page').then(
						(m) => m.CandidateApplicationsPage
					)
			},
			{
				path: 'applications/:applicationId',
				loadComponent: () =>
					import('./features/applications/pages/application-detail/candidate-application-detail.page').then(
						(m) => m.CandidateApplicationDetailPage
					)
			},
			{
				path: 'interviews/:sessionId/report',
				loadComponent: () =>
					import('./features/candidates/pages/interviews/candidate-interview-report.page').then(
						(m) => m.CandidateInterviewReportPage
					)
			},
			{
				path: 'interviews/:sessionId',
				canDeactivate: [interviewSessionLeaveGuard],
				loadComponent: () =>
					import('./features/candidates/pages/interviews/candidate-interview-session.page').then(
						(m) => m.CandidateInterviewSessionPage
					)
			},
			{
				path: 'interviews',
				loadComponent: () =>
					import('./features/candidates/pages/interviews/candidate-interviews.page').then(
						(m) => m.CandidateInterviewsPage
					)
			},
			{
				path: 'profile',
				loadComponent: () =>
					import('./features/candidates/pages/profile/candidate-profile.page').then(
						(m) => m.CandidateProfilePage
					)
			},
			{
				path: 'jobs/:jobId',
				loadComponent: () =>
					import('./features/candidates/pages/jobs/candidate-job-detail.page').then(
						(m) => m.CandidateJobDetailPage
					)
			},
			{
				path: 'jobs',
				loadComponent: () =>
					import('./features/candidates/pages/jobs/candidate-jobs.page').then(
						(m) => m.CandidateJobsPage
					)
			}
		]
	},
	{
		path: 'recruiter',
		component: RecruiterLayoutComponent,
		canActivate: [authGuard, roleGuard(['Recruiter', 'Admin'])],
		children: [
			{
				path: '',
				pathMatch: 'full',
				redirectTo: 'dashboard'
			},
			{
				path: 'profile',
				loadComponent: () =>
					import('./features/recruiters/pages/profile/recruiter-profile.page').then(
						(m) => m.RecruiterProfilePage
					)
			},
			{
				path: 'dashboard',
				loadComponent: () =>
					import('./features/recruiters/pages/dashboard/recruiter-dashboard.page').then(
						(m) => m.RecruiterDashboardPage
					)
			},
			{
				path: 'admin',
				canActivate: [roleGuard(['Admin'])],
				loadComponent: () =>
					import('./features/admin/pages/dashboard/admin-dashboard.page').then(
						(m) => m.AdminDashboardPage
					)
			},
			{
				path: 'jobs/create',
				loadComponent: () =>
					import('./features/recruiters/pages/jobs/recruiter-job-create.page').then(
						(m) => m.RecruiterJobCreatePage
					)
			},
			{
				path: 'jobs/:jobId/top-candidates',
				loadComponent: () =>
					import('./features/recruiters/pages/candidates/recruiter-top-candidates.page').then(
						(m) => m.RecruiterTopCandidatesPage
					)
			},
			{
				path: 'jobs/:jobId',
				loadComponent: () =>
					import('./features/recruiters/pages/jobs/recruiter-job-detail.page').then(
						(m) => m.RecruiterJobDetailPage
					)
			},
			{
				path: 'jobs',
				loadComponent: () =>
					import('./features/recruiters/pages/jobs/recruiter-jobs.page').then(
						(m) => m.RecruiterJobsPage
					)
			},
			{
				path: 'candidates/:candidateId',
				loadComponent: () =>
					import('./features/recruiters/pages/candidates/recruiter-candidate-detail.page').then(
						(m) => m.RecruiterCandidateDetailPage
					)
			},
			{
				path: 'candidates',
				loadComponent: () =>
					import('./features/recruiters/pages/candidates/recruiter-candidates.page').then(
						(m) => m.RecruiterCandidatesPage
					)
			},
			{
				path: 'applications',
				loadComponent: () =>
					import('./features/applications/pages/recruiter-applications/recruiter-applications.page').then(
						(m) => m.RecruiterApplicationsPage
					)
			},
			{
				path: 'applications/:applicationId',
				loadComponent: () =>
					import('./features/applications/pages/application-detail/recruiter-application-detail.page').then(
						(m) => m.RecruiterApplicationDetailPage
					)
			},
			{
				path: 'interviews/:sessionId/report',
				loadComponent: () =>
					import('./features/recruiters/pages/interviews/recruiter-interview-report.page').then(
						(m) => m.RecruiterInterviewReportPage
					)
			},
			{
				path: 'interviews/:sessionId',
				loadComponent: () =>
					import('./features/recruiters/pages/interviews/recruiter-interview-detail.page').then(
						(m) => m.RecruiterInterviewDetailPage
					)
			},
			{
				path: 'interviews',
				loadComponent: () =>
					import('./features/recruiters/pages/interviews/recruiter-interviews.page').then(
						(m) => m.RecruiterInterviewsPage
					)
			}
		]
	},
	{
		path: 'forbidden',
		loadComponent: () =>
			import('./shared/pages/forbidden/forbidden.page').then((m) => m.ForbiddenPage)
	},
	{
		path: '**',
		loadComponent: () => import('./shared/pages/not-found/not-found.page').then((m) => m.NotFoundPage)
	}
];
