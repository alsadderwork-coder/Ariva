# Cloud (Helm, Helmfile, Azure DevOps) rules

Mirrors AMAN's `Platform/Cloud` layout. Agents edit charts and pipelines; humans run releases.

- **Never** run `helm install|upgrade|uninstall`, `helmfile apply|sync|destroy` or mutating `kubectl` commands; the hooks block them. Validate with `helm template` and `helm lint` when Helm is installed.
- One template per host in `Ariva.K8s/Helm/Charts/platform/templates/`, following the AMAN shape (ConfigMap, Deployment, HPA, Service); appsettings are mounted from Kubernetes secrets, never committed.
- **Pod security (CWE-269):** every Deployment sets `securityContext` with `runAsNonRoot: true`, `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `seccompProfile: RuntimeDefault`; .NET hosts use `readOnlyRootFilesystem: true` with an `emptyDir` for `/tmp`. Images run as the non-root `app` user (`USER $APP_UID` in .NET Dockerfiles).
- **Images:** pin base images to a version (no `latest`); production values pin build numbers, never `trunk`.
- **Ingress:** TLS on every host (`tls:` section with the site certificate secret); no wildcard CORS at the ingress.
- **Forwarded headers:** never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`; configure `Security:ForwardedHeaders` with the ingress controller's pod network only.
- **Environments:** `values-k8s-dev.yaml`, `values-k8s-demo.yaml`, `values-k8s-prd.yaml`, `values-localk8s.yaml`; Helmfile selects them through `environments:`. The simulator is disabled in production.
- **Pipelines:** `Ariva.Cicd/AzureDevOps/Common/Analyze-solution.yaml` is the PR gate (security scan, build, unit, web, e2e). Build pipelines per service with path filters; release pipelines use a variable group for secrets.
