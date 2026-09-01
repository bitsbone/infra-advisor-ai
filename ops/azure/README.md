# Manual Azure/Datadog setup steps

These are one-off `az` CLI + Datadog UI steps, not automated in CI or Bicep —
matching this project's convention of treating sensitive IAM grants as
explicit, user-confirmed commands rather than something baked into
`build-push.yml`.

## Datadog Data Jobs Monitoring for Azure Data Factory

Prerequisite: the Datadog Azure integration (an App Registration with
subscription access) must already be installed — this reuses that same App
Registration, it does not provision a new one.

1. Create the least-privilege custom role (`datadog-adf-role.json` in this
   directory), replacing `<SUBSCRIPTION_ID>` with your actual subscription ID:

   ```bash
   az role definition create --role-definition ops/azure/datadog-adf-role.json
   ```

2. Assign it to the Datadog App Registration's client ID:

   ```bash
   az role assignment create \
     --assignee <DATADOG_APP_REGISTRATION_CLIENT_ID> \
     --role "Datadog ADF Monitoring (Custom)" \
     --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/rg-tola-infra-advisor-ai
   ```

3. In the Datadog UI: **Data Observability → Integrations → Azure Data
   Factory** → select the App Registration → select the subscription →
   select the `adf-infra-advisor-<environment>` factory.

Notes:
- This is a **Preview** Datadog feature.
- It's pull-based (Datadog polls the ADF management API) — no Event Hub or
  diagnostic settings needed.
- Dataset lineage only resolves for a specific connector allow-list that does
  **not** include Azure AI Search or Azure OpenAI — pipeline/activity status
  monitoring works fully regardless, lineage graphs just won't appear for the
  custom Function Activity steps. This is expected, not a bug to chase.
