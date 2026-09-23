# CI/CD Next Steps Plan

This document turns the current Azure deployment work into a practical next-step plan for a safer and more repeatable CI/CD process.

## Current State

Already in place:

- Azure staging environment is deployed and working
- `www.gretagames.com` is bound with an Azure-managed certificate
- `.github/workflows/deploy-azure.yml` exists
- the workflow tests both repos before deploy
- the workflow uses OIDC-based Azure login
- the workflow starts the `migrations` Azure Container Apps job after deploy

Current gap:

- the deployment path exists, but it still needs to be finished as an operational process you can trust and use regularly

## Goal

Move from "we can deploy" to "we can deploy safely, predictably, and repeatedly from GitHub."

## Recommended Rollout Order

1. Harden the existing staging workflow
2. Add automated post-deploy smoke checks
3. Document rollback and recovery
4. Add branch and approval rules
5. Create a separate production environment
6. Promote from staging to production with the same workflow shape

## Phase 1: Harden Staging CI/CD

Objective: make `azure-staging` the canonical deployment path instead of manual local deploys.

### Tasks

- Confirm the GitHub Environment `azure-staging` exists
- Populate all required environment secrets and variables
- Run the workflow manually from GitHub once end-to-end
- Verify the workflow succeeds without any local-machine credentials
- Treat that workflow as the default way to update staging

### Required GitHub Environment Settings

Environment name:

- `azure-staging`

Secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `WEB_REPO_READ_TOKEN` if the web repo is private

Variables:

- `AZURE_LOCATION=eastus2`
- `AZURE_RESOURCE_GROUP=rg-gretagames-staging`
- `AZURE_ENVIRONMENT_NAME=Staging`
- `CUSTOM_DOMAIN=www.gretagames.com`
- `CUSTOM_DOMAIN_CERTIFICATE_NAME=www-gretagames-com`

### Verification

- workflow runs from `workflow_dispatch`
- Azure login succeeds with OIDC
- tests pass in both repos
- `aspire deploy` completes successfully
- the migrations job runs successfully
- `https://www.gretagames.com` still returns `200`

## Phase 2: Add Post-Deploy Smoke Tests

Objective: fail the pipeline if the deployment is technically successful but the app is broken.

### Recommended Checks

After deploy and migrations:

- `curl -I https://www.gretagames.com`
- fetch the home page and assert HTML is returned
- hit a lightweight application route or health-like route exposed through `web`
- optionally run a Playwright smoke test against the live site

### Recommended Implementation

Add one or more workflow steps after the migrations step:

```sh
curl -fI https://www.gretagames.com
curl -fsS https://www.gretagames.com > /tmp/gretagames-home.html
test -s /tmp/gretagames-home.html
```

If you later add browser tests, keep them short:

- homepage loads
- main game page renders
- at least one leaderboard read works

## Phase 3: Add Rollback Documentation

Objective: make failure recovery straightforward.

### What to document

- how to redeploy the previous known-good commit
- how to re-run the workflow against a specific ref
- how to manually restart the `migrations` job if needed
- how to inspect Container Apps revisions
- how to fall back to the Azure default hostname if the custom domain has issues

### Minimum rollback playbook

- identify the last good Git commit
- re-run the workflow from that commit
- verify `www.gretagames.com`
- verify database migrations did not introduce incompatible changes

## Phase 4: Protect The Deployment Process

Objective: prevent accidental deploys and require an intentional release step.

### Recommended GitHub settings

- require approval on the `azure-staging` environment
- protect `main`
- require tests to pass before merge
- allow deploy only from `main` or tagged releases

### Optional workflow improvements

- add path filters so deploys run only when relevant repos/files change
- support both `workflow_dispatch` and `push` to `main`
- add a manual boolean input like `run_deploy=true`

## Phase 5: Create A Production Environment

Objective: separate staging from production instead of treating the current environment as both.

### Recommended Production Shape

Create a second environment with its own:

- resource group
- managed environment
- PostgreSQL server
- Application Insights instance
- custom domain
- GitHub environment

Suggested names:

- resource group: `rg-gretagames-prod`
- GitHub environment: `azure-production`
- Aspire environment name: `Production`

### Why separate it

- safer releases
- safer schema changes
- real staging before production
- easier incident response

## Phase 6: Promotion Strategy

Objective: reduce risk by promoting the same code that already passed staging.

### Recommended strategy

- merge to `main`
- deploy automatically or manually to staging
- run smoke tests
- approve promotion
- deploy the same commit SHA to production

### Best practice

Do not build different artifacts for staging and production from different commits.

Promote the same tested revision.

## Suggested Near-Term Improvements

If you want the highest-value next actions, do these first:

1. Run the GitHub Actions deploy once using OIDC and confirm it works without local auth.
2. Add post-deploy smoke checks to the workflow.
3. Add GitHub environment approval for `azure-staging`.
4. Decide whether `gretagames.com` should later redirect to `www.gretagames.com`.
5. Plan a separate production environment before frequent public releases.

## Recommended Backlog

### Short term

- make GitHub Actions the primary staging deploy method
- add smoke tests
- add rollback notes
- verify App Insights dashboards and alerts

### Medium term

- create `azure-production`
- add deployment approvals
- add release tags or versioned deploys
- add uptime and failure alerts

### Longer term

- add database backup automation
- add scheduled restore testing
- add richer browser regression tests
- add release notes or deployment summaries

## Proposed Workflow End State

Target state:

1. developer merges to `main`
2. tests run
3. staging deploy runs through GitHub OIDC
4. migrations job runs
5. smoke checks run
6. human approval gates production
7. production deploy runs against a separate environment

That gives you a repeatable deployment pipeline with testing, environment separation, and a cleaner release story than local manual deploys.
