export const environment = {
  production: true,
  apiBaseUrl: 'http://localhost:4200',
  apiPrefix: '/api',
  realtimeHubUrl: 'http://localhost:4200/hubs/interviews',
  authStorageKeys: {
    accessToken: 'joblens.accessToken',
    refreshToken: 'joblens.refreshToken',
    authUser: 'joblens.authUser',
  },
} as const;
