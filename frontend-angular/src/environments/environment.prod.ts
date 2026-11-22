export const environment = {
  production: true,
  apiBaseUrl: '',
  apiPrefix: '/api',
  realtimeHubUrl: '/hubs/interview',
  chatHubUrl: '/hubs/chat',
  authStorageKeys: {
    accessToken: 'joblens.accessToken',
    refreshToken: 'joblens.refreshToken',
    authUser: 'joblens.authUser',
  },
} as const;
