# BobTools Azure OCR Infrastructure

This folder contains a first-pass Bicep deployment for a brokered BobTools OCR backend with these components:

- API Management as the public edge and shared client-key gate
- Azure Function App as the OCR broker backend
- Key Vault for the real Azure Vision secret and BobTools shared key
- Storage Account for Function runtime state
- Application Insights plus Log Analytics for observability

## Public Exposure Model

This deployment is intentionally minimal-public rather than fully private:

- API Management is public and is the intended entry point for the BobTools desktop app.
- The Function App is linked to API Management and deployed with `hostNamesDisabled: true` so it is not meant to be called directly.
- Key Vault stays on public networking for this first pass, but access is restricted by RBAC and the Function App system-assigned managed identity.

This keeps the topology simpler and cheaper than a full private-endpoint/VNet design while still avoiding shipping the real Azure Vision secret in the desktop client.

## Inputs

You need to provide three important values at deployment time:

- `azureVisionEndpoint`
- `azureVisionKey`
- `bobToolsClientPrimaryKey`

The Bicep stores those values in Key Vault during deployment.

## Deploy

Example with Azure CLI:

```bash
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.example.json
```

For a safer workflow, pass the secret values on the command line or from a secure pipeline variable instead of editing them into a committed parameters file.

Example:

```bash
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.example.json \
  --parameters azureVisionKey=<vision-key> \
  --parameters bobToolsClientPrimaryKey=<shared-client-key>
```

## What The Deployment Creates

- A workspace-based Application Insights instance
- A Log Analytics workspace
- A general-purpose storage account for Functions
- A consumption App Service plan (`Y1`)
- A Windows Function App configured for `.NET isolated`
- A Key Vault with three secrets:
  - Azure Vision endpoint
  - Azure Vision key
  - BobTools shared client key
- An API Management instance with:
  - one API at `/<apimApiPath>`
  - one product for BobTools clients
  - one shared subscription key that matches `bobToolsClientPrimaryKey`

## Expected Backend Shape

The deployment assumes the Function App will eventually expose at least these endpoints:

- `GET /api/health`
- `POST /api/scan`

Those are the two operations pre-created in API Management.

## App Settings Expected By The Function

The Function App receives these settings through Key Vault references:

- `AzureVision__Endpoint`
- `AzureVision__ApiKey`
- `BobTools__SharedAccessKey`
- `FUNCTIONS_EXTENSION_VERSION`
- `FUNCTIONS_WORKER_RUNTIME`

## Notes

- The APIM SKU defaults to `Developer` because it is a pragmatic non-production starting point. If you are optimizing for cost or production readiness, set a different SKU in the parameters.
- This deployment does not publish Function code. It only provisions the hosting and secret plumbing.
- If you later want stronger isolation, the next step is private endpoints and VNet integration for Key Vault, Function App, and API Management.