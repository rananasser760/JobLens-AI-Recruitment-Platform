export type UserRole = 'Candidate' | 'Recruiter' | 'Admin';

export interface RegisterDto {
  username: string;
  email: string;
  password: string;
  confirmPassword: string;
  role: 'Candidate' | 'Recruiter';
  fullName?: string;
  phone?: string;
  companyId?: number;
}

export interface LoginDto {
  email: string;
  password: string;
}

export interface RefreshTokenDto {
  accessToken: string;
  refreshToken: string;
}

export interface AuthResponseDto {
  userId: number;
  username: string;
  email: string;
  role: UserRole;
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  profileId?: number | null;
  fullName?: string | null;
}

export interface ChangePasswordDto {
  currentPassword: string;
  newPassword: string;
  confirmNewPassword: string;
}

export interface ForgotPasswordDto {
  email: string;
}

export interface ResetPasswordDto {
  token: string;
  email: string;
  newPassword: string;
  confirmNewPassword: string;
}
