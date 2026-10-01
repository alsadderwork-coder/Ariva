FROM node:22-alpine AS builder
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


FROM nginx:1.28

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
