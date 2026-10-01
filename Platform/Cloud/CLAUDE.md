# Cloud (Helm, Helmfile, pipelines) rules

Mirrors AMAN's `Platform/Cloud` layout. Agents edit charts and pipelines; humans run releases.

- **Never** run `helm install|upgrade|uninstall`, `helmfile apply|sync|destroy` or mutating `kubectl` commands; the hooks block them. Validate with `helm template` and `helm lint` when Helm is installed.
- One template per host in `Ariva.K8s/Helm/Charts/platform/templates/`, following the AMAN shape (ConfigMap, Deployment, HPA, Service); appsettings are mounted from Kubernetes secrets, never committed.
- **Pod security (CWE-269):** every Deployment sets `securityContext` with `runAsNonRoot: true`, `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `seccompProfile: RuntimeDefault`; .NET hosts use `readOnlyRootFilesystem: true` with an `emptyDir` for `/tmp`. Images run as the non-root `app` user (`USER $APP_UID` in .NET Dockerfiles).
- **Images:** pin base images to a version (no `latest`); production values pin build numbers, never `trunk`.
- **Ingress:** TLS on every host (`tls:` section with the site certificate secret); no wildcard CORS at the ingress.
- **Forwarded headers:** never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`; configure `Security:ForwardedHeaders` with the ingress controller's pod network only.
- **Environments:** `values-k8s-dev.yaml`, `values-k8s-demo.yaml`, `values-k8s-prd.yaml`, `values-localk8s.yaml`; Helmfile selects them through `environments:`. The simulator is disabled in production.
- **Pipelines:** GitHub Actions in `.github/workflows` are the gate: `ci.yml` (security scan, build, unit, web, e2e) on every pull request, `security-scan.yml` (Trivy, Semgrep CE, SBOM), `codeql.yml` (only with Advanced Security), `images.yml` (build, Trivy image scan, push to GHCR on `v*` tags). Pin every action to a full commit SHA with the version in a comment. `Ariva.Cicd/AzureDevOps` keeps AMAN-style build and release pipelines for deployments that must publish to Dalil Container Registry; keep both in step when images or paths change.
