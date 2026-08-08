# Multi-stage build: compile the Vite SPA, then serve it from nginx with the
# API reverse-proxied under /api so the browser only ever talks to one origin.
FROM node:20-alpine AS build
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
EXPOSE 80
