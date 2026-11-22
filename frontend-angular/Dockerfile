FROM node:20-alpine AS build
WORKDIR /app

COPY package.json package-lock.json* ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --no-audit --no-fund || npm install --no-audit --no-fund

ENV NG_CLI_ANALYTICS=false \
    CI=true
COPY . .
RUN --mount=type=cache,target=/app/.angular/cache \
    npm run build

FROM nginx:alpine
COPY --from=build /app/dist/GP_front/browser /usr/share/nginx/html

RUN echo 'server { \
    listen 4000; \
    include /etc/nginx/mime.types; \
    location / { \
        root /usr/share/nginx/html; \
        index index.html index.htm; \
        try_files $uri $uri/ /index.html; \
    } \
    location ~* \.(?:css|js|woff2?|svg|png|jpg|ico)$ { \
        root /usr/share/nginx/html; \
        expires 1y; \
        add_header Cache-Control "public, immutable"; \
    } \
}' > /etc/nginx/conf.d/default.conf

EXPOSE 4000
CMD ["nginx", "-g", "daemon off;"]