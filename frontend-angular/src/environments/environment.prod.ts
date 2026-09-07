export const environment = {
  production: true,
  apiBaseUrl: 'http://localhost:5245',
  apiPrefix: '/api',
  realtimeHubUrl: 'http://localhost:5245/hubs/interview',
  chatHubUrl: 'http://localhost:5245/hubs/chat',
  authStorageKeys: {
    accessToken: 'joblens.accessToken',
    refreshToken: 'joblens.refreshToken',
    authUser: 'joblens.authUser',
  },
} as const;
