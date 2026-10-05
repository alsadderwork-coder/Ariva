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


# nginx stable line (1.30) on Alpine: 1.28 stopped receiving image rebuilds and its Debian base carried 120+ high or
# critical OS findings. apk upgrade picks up Alpine security fixes released after the upstream image was built.
FROM nginx:1.31-alpine3.24@sha256:df221db836e1754089190208cee7eeda94f233197056426eda74a43ab1abeac2

RUN apk upgrade --no-cache

COPY --from=builder /app/build/nginx/default.conf /etc/nginx/conf.d
COPY --from=builder /app/build /usr/share/nginx/html/web

RUN addgroup -S -g 10001 nonroot \
    && adduser -S -D -H -u 10001 -G nonroot nonroot \
    && chown -R nonroot:nonroot /usr/share/nginx/html/web \
    && chown -R nonroot:nonroot /var/cache/nginx \
    && chown -R nonroot:nonroot /var/log/nginx \
    && touch /var/run/nginx.pid \
    && chown -R nonroot:nonroot /var/run/nginx.pid

USER 10001
EXPOSE 3000
