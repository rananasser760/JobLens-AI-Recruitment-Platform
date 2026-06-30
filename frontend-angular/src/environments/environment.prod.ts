export const environment = {
  production: true,
  apiBaseUrl: 'https://api.joblens.local',
  apiPrefix: '/api',
  realtimeHubUrl: 'https://api.joblens.local/hubs/interviews',
  authStorageKeys: {
    accessToken: 'joblens.accessToken',
    refreshToken: 'joblens.refreshToken',
    authUser: 'joblens.authUser'
  }
} as const;
