#!/usr/bin/env bash
# One-time setup for the deploy workflow (chapter 14): an identity GitHub Actions signs in as, with
# OpenID Connect. There is no client secret: Entra ID trusts tokens that GitHub issues for this
# repository's "production" environment, and only those.
#
#   infra/bootstrap/github-oidc.sh <subscription-id> <owner/repo> <environment-name> <location>
#
# Afterwards, set the repository variables it prints (Settings > Secrets and variables > Actions >
# Variables). They are identifiers, not secrets.
set -euo pipefail

subscription="$1"
repo="$2"
env_name="$3"
location="$4"
rg="rg-priorauthbot-${env_name}"
identity="id-priorauthbot-${env_name}-deploy"

az account set --subscription "$subscription"
az group create --name "$rg" --location "$location" --output none

# A user-assigned managed identity rather than an app registration with a secret.
az identity create --resource-group "$rg" --name "$identity" --output none
client_id=$(az identity show -g "$rg" -n "$identity" --query clientId -o tsv)
principal_id=$(az identity show -g "$rg" -n "$identity" --query principalId -o tsv)

# Trust GitHub's tokens for jobs that run in the repository's "production" environment. A job on a
# fork, a pull request, or another branch gets a token with a different subject and is refused.
az identity federated-credential create --resource-group "$rg" --identity-name "$identity" \
  --name github-production \
  --issuer https://token.actions.githubusercontent.com \
  --subject "repo:${repo}:environment:production" \
  --audiences api://AzureADTokenExchange --output none

# What the pipeline may do: deploy resources in this resource group, assign the roles the template
# grants (constrained to those roles), and push images. Not Owner, and nothing outside the group.
scope="/subscriptions/${subscription}/resourceGroups/${rg}"
az role assignment create --assignee-object-id "$principal_id" --assignee-principal-type ServicePrincipal \
  --role Contributor --scope "$scope" --output none
az role assignment create --assignee-object-id "$principal_id" --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope "$scope" --output none \
  --condition-version 2.0 \
  --condition "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {7f951dda-4ed3-4680-a7ca-43fe172d538d, 5e0bd9bd-7b93-4f28-af87-19fc36ad61bd, ba92f5b4-2d11-453d-a403-e96b0029c9fe, 4633458b-17de-408a-b874-0445c86b69e6, 3913510d-42f4-4e42-8a64-420c390055eb, 8ebe5a00-799e-43f5-93ac-243d3dce84a7, 7ca78c08-252a-4471-8644-bb5ff32d4ba0, 00482a5a-887f-4fb3-b363-3b7fe8e74483})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {7f951dda-4ed3-4680-a7ca-43fe172d538d, 5e0bd9bd-7b93-4f28-af87-19fc36ad61bd, ba92f5b4-2d11-453d-a403-e96b0029c9fe, 4633458b-17de-408a-b874-0445c86b69e6, 3913510d-42f4-4e42-8a64-420c390055eb, 8ebe5a00-799e-43f5-93ac-243d3dce84a7, 7ca78c08-252a-4471-8644-bb5ff32d4ba0, 00482a5a-887f-4fb3-b363-3b7fe8e74483}))"

tenant_id=$(az account show --query tenantId -o tsv)
cat <<VARS

Set these repository variables (not secrets):
  AZURE_CLIENT_ID=${client_id}
  AZURE_TENANT_ID=${tenant_id}
  AZURE_SUBSCRIPTION_ID=${subscription}
  AZURE_ENV_NAME=${env_name}
  AZURE_LOCATION=${location}
  AZURE_RESOURCE_GROUP=${rg}
  PRIORAUTHBOT_API_CLIENT_ID=<appId output of infra/entra/app-registration.bicep>

Then create the GitHub environment "production" with required reviewers (Settings > Environments),
and "production-override" with the security lead as its only reviewer (see docs/chapters/ch14.md).
VARS
