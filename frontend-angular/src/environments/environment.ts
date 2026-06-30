export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5245',
  apiPrefix: '/api',
  realtimeHubUrl: 'http://localhost:5245/hubs/interviews',
  authStorageKeys: {
    accessToken: 'joblens.accessToken',
    refreshToken: 'joblens.refreshToken',
    authUser: 'joblens.authUser'
  }
} as const;
