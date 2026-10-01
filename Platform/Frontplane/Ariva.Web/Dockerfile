FROM node:22.23.3-alpine3.24@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS builder
WORKDIR /app
ENV NODE_OPTIONS=--max-old-space-size=4096
COPY ./Platform/Frontplane/Ariva.Web/package*.json .
RUN --mount=type=cache,id=npm,target=/root/.npm \
    npm set cache /root/.npm && \
    npm install --force

COPY ./Platform/Frontplane/Ariva.Web .
RUN --mount=type=cache,id=npm,target=/root/.npm \
    npm set cache /root/.npm && \
    npm run build --prod


FROM nginx:1.28.3@sha256:146adea4768b83c607d0bdfa4188464e3da6e0a3ad4475db1d1d8f64f27c29cc

COPY --from=builder /app/build/nginx/default.conf /etc/nginx/conf.d
COPY --from=builder /app/build /usr/share/nginx/html/web

RUN groupadd -r -g 10001 nonroot \
    && useradd -r -u 10001 -g nonroot nonroot \
    && chown -R nonroot:nonroot /usr/share/nginx/html/web \
    && chown -R nonroot:nonroot /var/cache/nginx \
    && chown -R nonroot:nonroot /var/log/nginx \
    && touch /var/run/nginx.pid \
    && chown -R nonroot:nonroot /var/run/nginx.pid

USER 10001
EXPOSE 3000
