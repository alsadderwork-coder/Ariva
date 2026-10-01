# Cloud (Helm, Helmfile, pipelines) rules

Mirrors AMAN's `Platform/Cloud` layout. Agents edit charts and pipelines; humans run releases.

- **Never** run `helm install|upgrade|uninstall`, `helmfile apply|sync|destroy` or mutating `kubectl` commands; the hooks block them. Validate with `helm template` and `helm lint` when Helm is installed.
- One template per host in `Ariva.K8s/Helm/Charts/platform/templates/`, following the AMAN shape (ConfigMap, Deployment, HPA, Service); appsettings are mounted from Kubernetes secrets, never committed.
- **Pod security (CWE-269):** every workload sets `runAsNonRoot: true`, `runAsUser: 10001`, `seccompProfile: RuntimeDefault`, and each container `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `readOnlyRootFilesystem: true` with an `emptyDir` at `/tmp` (the web pod also mounts `/var/cache/nginx` and `/run`). Images run as numeric user 10001. `node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs` renders every environment and fails on any gap (runs in `verify security` and CI).
- **Images:** base images pinned by full version tag and digest (`node scripts/base-images.mjs --check`; the `base-images` workflow proposes new pins and Dependabot keeps them current); no `latest` anywhere; production refuses to render without an explicit build number.
- **Ingress:** TLS on every host (`tls:` with `tlsSecretName`, forced HTTPS redirect); no wildcard CORS at the ingress.
- **Forwarded headers:** never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`; configure `Security:ForwardedHeaders` with the ingress controller's pod network only.
- **Environments:** `helmfile-k8s.yaml.gotmpl -e dev|demo|prd|localk8s` selects `values-k8s-dev.yaml`, `values-k8s-demo.yaml`, `values-k8s-prd.yaml` or `values-localk8s.yaml`. The simulator is disabled in production.
- **Pipelines:** GitHub Actions in `.github/workflows` are the gate: `ci.yml` (security scan, build, unit, web, e2e) on every pull request, `security-scan.yml` (Trivy, Semgrep CE, SBOM), `codeql.yml` (only with Advanced Security), `images.yml` (build, Trivy image scan, push to GHCR on `v*` tags). Pin every action to a full commit SHA with the version in a comment. `Ariva.Cicd/AzureDevOps` keeps AMAN-style build and release pipelines for deployments that must publish to Dalil Container Registry; keep both in step when images or paths change.
