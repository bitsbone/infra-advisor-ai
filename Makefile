.PHONY: deploy-infra deploy-k8s check-env create-ghcr-secret create-airflow-ghcr-secret create-airflow-secret create-mcp-server-secret create-mcp-server-dotnet-secret create-agent-api-secret create-agent-api-dotnet-secret create-load-generator-secret create-postgres-secret create-redis-secret create-auth-api-secret create-dd-postgres-secret create-mailpit-secret create-secrets redeploy-mailpit setup-postgres-dbm run-dags apply-datadog-agent install-airflow recover-airflow-destructive preflight-airflow-cluster verify-airflow-image upgrade-airflow sync-dags build-airflow-image test-airflow test-airflow-container otel-poc run-otel-poc build-otel-poc otel-maf-poc run-otel-maf-poc build-otel-maf-poc start-otel-collector stop-otel-collector logs-otel-collector run-ios run-android run-prism-ios help

# Load .env for normal local operation. Set SKIP_DOTENV=1 for documentation,
# static analysis, and dry runs so Make never expands local credentials.
ifneq ($(SKIP_DOTENV),1)
-include .env
export
endif

RESOURCE_GROUP ?= rg-tola-infra-advisor-ai
AKS_NAME ?= aks-infra-advisor-dev
LOCATION ?= eastus
NAMESPACE ?= infra-advisor
GHCR_PAT ?=
GITHUB_EMAIL ?=
AIRFLOW_CHART_VERSION ?= 1.21.0
AIRFLOW_IMAGE_REPOSITORY ?= ghcr.io/kyletaylored/infra-advisor-ai/airflow
AIRFLOW_IMAGE_TAG ?= latest
AIRFLOW_NAMESPACE ?= airflow
AIRFLOW_DESTRUCTIVE_RECOVERY ?=
MAUI_PROJECT ?= mobile/cross-platform/maui/src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj
MAUI_IOS_CONFIGURATION ?= Debug
MAUI_IOS_RUNTIME_IDENTIFIER ?= iossimulator-arm64
MAUI_IOS_APP ?= mobile/cross-platform/maui/src/InfraAdvisor.Mobile/bin/$(MAUI_IOS_CONFIGURATION)/net10.0-ios/$(MAUI_IOS_RUNTIME_IDENTIFIER)/InfraAdvisor.Mobile.app
MAUI_IOS_BUNDLE_ID ?= dev.kyletaylor.infraadvisor.maui
PRISM_SAMPLE_ROOT ?= _reference/Prism-Samples-Maui
PRISM_SAMPLE_PROJECT ?= sample-template/PrismSample/PrismSample.csproj
PRISM_SAMPLE_APP ?= sample-template/PrismSample/bin/Debug/net8.0-ios/iossimulator-arm64/PrismSample.app
PRISM_XCODE_DEVELOPER_DIR ?= /Applications/Xcode-16.4.0.app/Contents/Developer
PRISM_IOS_RUNTIME ?= 18.4
PRISM_IOS_SIMULATOR_UDID ?=
IOS_SIMULATOR_UDID ?=

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | awk 'BEGIN {FS = ":.*?## "}; {printf "\033[36m%-20s\033[0m %s\n", $$1, $$2}'

# ─── .NET MAUI mobile app ─────────────────────────────────────────────────────────────────────────

run-ios: ## Run the MAUI app on the booted iOS simulator (IOS_SIMULATOR_UDID optional)
	@command -v dotnet >/dev/null || { echo "ERROR: dotnet is not installed"; exit 1; }
	@command -v xcrun >/dev/null || { echo "ERROR: Xcode command-line tools are not installed"; exit 1; }
	@set -e; \
	SIMULATOR_UDID="$(IOS_SIMULATOR_UDID)"; \
	if [ -z "$$SIMULATOR_UDID" ]; then \
		SIMULATOR_UDID="$$(xcrun simctl list devices booted | awk -F '[()]' '/Booted/ { print $$2; exit }')"; \
	fi; \
	if [ -z "$$SIMULATOR_UDID" ]; then \
		echo "ERROR: No booted iOS simulator found. Start one or run make run-ios IOS_SIMULATOR_UDID=<UDID>."; \
		exit 1; \
	fi; \
	SDK_VERSION="$$(xcrun --sdk iphonesimulator --show-sdk-version)"; \
	if ! xcrun simctl list runtimes available | grep -q "iOS $$SDK_VERSION"; then \
		echo "ERROR: The selected Xcode provides iOS SDK $$SDK_VERSION, but that simulator runtime is not installed."; \
		echo "Install iOS $$SDK_VERSION in Xcode → Settings → Components, or select an Xcode whose SDK matches an installed runtime."; \
		exit 1; \
	fi; \
	echo "→ Building current InfraAdvisor sources"; \
	dotnet build "$(MAUI_PROJECT)" -f net10.0-ios -c "$(MAUI_IOS_CONFIGURATION)" -p:InfraAdvisorBuildPlatform=ios -p:RuntimeIdentifier="$(MAUI_IOS_RUNTIME_IDENTIFIER)"; \
	echo "→ Replacing InfraAdvisor on $$SIMULATOR_UDID"; \
	xcrun simctl terminate "$$SIMULATOR_UDID" "$(MAUI_IOS_BUNDLE_ID)" 2>/dev/null || true; \
	xcrun simctl uninstall "$$SIMULATOR_UDID" "$(MAUI_IOS_BUNDLE_ID)" 2>/dev/null || true; \
	xcrun simctl install "$$SIMULATOR_UDID" "$(MAUI_IOS_APP)"; \
	xcrun simctl launch --terminate-running-process "$$SIMULATOR_UDID" "$(MAUI_IOS_BUNDLE_ID)"

run-android: ## Run the MAUI app on the connected Android emulator
	@command -v dotnet >/dev/null || { echo "ERROR: dotnet is not installed"; exit 1; }
	@dotnet build "$(MAUI_PROJECT)" -f net10.0-android -t:Run -p:InfraAdvisorBuildPlatform=android

run-prism-ios: ## Run the Prism MAUI reference app on a compatible iOS simulator
	@command -v dotnet >/dev/null || { echo "ERROR: dotnet is not installed"; exit 1; }
	@command -v xcrun >/dev/null || { echo "ERROR: Xcode command-line tools are not installed"; exit 1; }
	@if [ ! -d "$(PRISM_XCODE_DEVELOPER_DIR)" ]; then echo "ERROR: Xcode 16.4 was not found at $(PRISM_XCODE_DEVELOPER_DIR)."; exit 1; fi
	@SIMULATOR_UDID="$(PRISM_IOS_SIMULATOR_UDID)"; \
	if [ -z "$$SIMULATOR_UDID" ]; then \
		SIMULATOR_UDID="$$(DEVELOPER_DIR="$(PRISM_XCODE_DEVELOPER_DIR)" xcrun simctl list devices available | awk -F '[()]' -v runtime="$(PRISM_IOS_RUNTIME)" '$$0 == "-- iOS " runtime " --" { active=1; next } /^-- / { active=0 } active && /Booted/ { print $$2; exit }')"; \
	fi; \
	if [ -z "$$SIMULATOR_UDID" ]; then \
		SIMULATOR_UDID="$$(DEVELOPER_DIR="$(PRISM_XCODE_DEVELOPER_DIR)" xcrun simctl list devices available | awk -F '[()]' -v runtime="$(PRISM_IOS_RUNTIME)" '$$0 == "-- iOS " runtime " --" { active=1; next } /^-- / { active=0 } active && /iPhone/ { print $$2; exit }')"; \
	fi; \
	if [ -z "$$SIMULATOR_UDID" ]; then \
		echo "ERROR: No iOS $(PRISM_IOS_RUNTIME) simulator found. Install that runtime or set PRISM_IOS_SIMULATOR_UDID=<UDID>."; \
		exit 1; \
	fi; \
	DEVELOPER_DIR="$(PRISM_XCODE_DEVELOPER_DIR)" xcrun simctl boot "$$SIMULATOR_UDID" 2>/dev/null || true; \
	DEVELOPER_DIR="$(PRISM_XCODE_DEVELOPER_DIR)" xcrun simctl bootstatus "$$SIMULATOR_UDID" -b; \
	open "$(PRISM_XCODE_DEVELOPER_DIR)/Applications/Simulator.app" --args -CurrentDeviceUDID "$$SIMULATOR_UDID"; \
	echo "→ Running Prism MAUI sample on $$SIMULATOR_UDID"; \
	cd "$(PRISM_SAMPLE_ROOT)" && \
	DEVELOPER_DIR="$(PRISM_XCODE_DEVELOPER_DIR)" dotnet build "$(PRISM_SAMPLE_PROJECT)" -f net8.0-ios -c Debug -p:TargetFrameworks=net8.0-ios -p:CheckEolWorkloads=false -p:SkipPrismPreviewAssets=true -p:ValidateXcodeVersion=false -p:RuntimeIdentifier=iossimulator-arm64 && \
	DEVELOPER_DIR="$(PRISM_XCODE_DEVELOPER_DIR)" xcrun simctl install "$$SIMULATOR_UDID" "$(PRISM_SAMPLE_APP)" && \
	DEVELOPER_DIR="$(PRISM_XCODE_DEVELOPER_DIR)" xcrun simctl launch --terminate-running-process "$$SIMULATOR_UDID" com.prismlibrary.prismsample

check-env: ## Verify all required env vars are set before deploying
	@echo "→ Checking required environment variables..."
	@for var in \
		AZURE_OPENAI_ENDPOINT AZURE_OPENAI_API_KEY \
		AZURE_SEARCH_ENDPOINT AZURE_SEARCH_API_KEY AZURE_STORAGE_CONNECTION_STRING \
		EIA_API_KEY SAMGOV_API_KEY DD_API_KEY \
		GHCR_PAT GITHUB_EMAIL \
		POSTGRES_USER POSTGRES_PASSWORD POSTGRES_DB \
		DD_POSTGRES_PASSWORD \
		DATABASE_URL JWT_SECRET \
		AIRFLOW_ADMIN_USERNAME AIRFLOW_ADMIN_PASSWORD \
		MAILPIT_UI_USERNAME MAILPIT_UI_PASSWORD; do \
		if [ -z "$$(eval echo \$$$$var)" ]; then \
			echo "  ERROR: $$var is not set"; \
			MISSING=1; \
		else \
			echo "  ✓ $$var"; \
		fi; \
	done; \
	if [ -n "$$MISSING" ]; then echo ""; echo "Set missing vars in .env and re-run."; exit 1; fi
	@echo "✓ All required env vars present"

# ─── Azure Infrastructure ──────────────────────────────────────────────────────

deploy-infra: ## Deploy Azure Bicep IaC (AKS, AI Search, OpenAI, etc.)
	@echo "→ Deploying Azure infrastructure (subscription-scoped)..."
	az deployment sub create \
		--location $(LOCATION) \
		--template-file infra/bicep/main.bicep \
		--parameters infra/bicep/parameters/dev.bicepparam \
		--verbose
	@echo "✓ Azure infrastructure deployed"

get-credentials: ## Fetch AKS kubeconfig
	az aks get-credentials \
		--resource-group $(RESOURCE_GROUP) \
		--name $(AKS_NAME) \
		--overwrite-existing
	@echo "✓ kubeconfig updated"

# ─── Kubernetes ───────────────────────────────────────────────────────────────

create-airflow-secret: ## Create airflow-azure-secret K8s Secret in airflow namespace
	@if [ -z "$(AZURE_OPENAI_ENDPOINT)" ]; then echo "ERROR: AZURE_OPENAI_ENDPOINT is not set"; exit 1; fi
	@if [ -z "$(AZURE_STORAGE_CONNECTION_STRING)" ]; then echo "ERROR: AZURE_STORAGE_CONNECTION_STRING is not set"; exit 1; fi
	@if [ -z "$(EIA_API_KEY)" ]; then echo "ERROR: EIA_API_KEY is not set"; exit 1; fi
	@if [ -z "$(SAMGOV_API_KEY)" ]; then echo "ERROR: SAMGOV_API_KEY is not set"; exit 1; fi
	@if [ -z "$(DD_API_KEY)" ]; then echo "ERROR: DD_API_KEY is not set (required for DJM OpenLineage transport)"; exit 1; fi
	@if [ -z "$(AIRFLOW_WEBSERVER_SECRET_KEY)" ]; then echo "ERROR: AIRFLOW_WEBSERVER_SECRET_KEY is not set — generate with: python3 -c \"import secrets; print(secrets.token_hex(32))\""; exit 1; fi
	@kubectl create namespace $(AIRFLOW_NAMESPACE) --dry-run=client -o yaml | kubectl apply -f -
	@kubectl create secret generic airflow-azure-secret \
		--namespace $(AIRFLOW_NAMESPACE) \
		--from-literal=AZURE_OPENAI_ENDPOINT="$(AZURE_OPENAI_ENDPOINT)" \
		--from-literal=AZURE_OPENAI_API_KEY="$(AZURE_OPENAI_API_KEY)" \
		--from-literal=AZURE_SEARCH_ENDPOINT="$(AZURE_SEARCH_ENDPOINT)" \
		--from-literal=AZURE_SEARCH_API_KEY="$(AZURE_SEARCH_API_KEY)" \
		--from-literal=AZURE_STORAGE_CONNECTION_STRING="$(AZURE_STORAGE_CONNECTION_STRING)" \
		--from-literal=EIA_API_KEY="$(EIA_API_KEY)" \
		--from-literal=SAMGOV_API_KEY="$(SAMGOV_API_KEY)" \
		--from-literal=DD_API_KEY="$(DD_API_KEY)" \
		--from-literal=webserver-secret-key="$(AIRFLOW_WEBSERVER_SECRET_KEY)" \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ airflow-azure-secret created in namespace $(AIRFLOW_NAMESPACE)"

create-mcp-server-secret: ## Create mcp-server-secret K8s Secret (Azure, EIA, ERCOT, SAM.gov keys)
	@if [ -z "$(AZURE_SEARCH_ENDPOINT)" ];  then echo "ERROR: AZURE_SEARCH_ENDPOINT is not set";  exit 1; fi
	@if [ -z "$(AZURE_SEARCH_API_KEY)" ];   then echo "ERROR: AZURE_SEARCH_API_KEY is not set";   exit 1; fi
	@if [ -z "$(AZURE_OPENAI_ENDPOINT)" ];  then echo "ERROR: AZURE_OPENAI_ENDPOINT is not set";  exit 1; fi
	@if [ -z "$(AZURE_OPENAI_API_KEY)" ];   then echo "ERROR: AZURE_OPENAI_API_KEY is not set";   exit 1; fi
	@if [ -z "$(EIA_API_KEY)" ];            then echo "ERROR: EIA_API_KEY is not set";            exit 1; fi
	@if [ -z "$(ERCOT_API_KEY)" ];          then echo "WARN: ERCOT_API_KEY is not set — ERCOT tool will be disabled"; fi
	@if [ -z "$(SAMGOV_API_KEY)" ];         then echo "WARN: SAMGOV_API_KEY is not set — procurement opportunities tool will be disabled"; fi
	@kubectl create secret generic mcp-server-secret \
		--namespace $(NAMESPACE) \
		--from-literal=AZURE_SEARCH_ENDPOINT="$(AZURE_SEARCH_ENDPOINT)" \
		--from-literal=AZURE_SEARCH_API_KEY="$(AZURE_SEARCH_API_KEY)" \
		--from-literal=AZURE_OPENAI_ENDPOINT="$(AZURE_OPENAI_ENDPOINT)" \
		--from-literal=AZURE_OPENAI_API_KEY="$(AZURE_OPENAI_API_KEY)" \
		--from-literal=EIA_API_KEY="$(EIA_API_KEY)" \
		--from-literal=ERCOT_API_KEY="$(ERCOT_API_KEY)" \
		--from-literal=SAMGOV_API_KEY="$(SAMGOV_API_KEY)" \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ mcp-server-secret created in namespace $(NAMESPACE)"

create-mcp-server-dotnet-secret: ## Create mcp-server-dotnet-secret K8s Secret (Azure Search + OpenAI + optional API keys)
	@if [ -z "$(AZURE_SEARCH_ENDPOINT)" ];  then echo "ERROR: AZURE_SEARCH_ENDPOINT is not set";  exit 1; fi
	@if [ -z "$(AZURE_SEARCH_API_KEY)" ];   then echo "ERROR: AZURE_SEARCH_API_KEY is not set";   exit 1; fi
	@if [ -z "$(AZURE_OPENAI_ENDPOINT)" ];  then echo "ERROR: AZURE_OPENAI_ENDPOINT is not set";  exit 1; fi
	@if [ -z "$(AZURE_OPENAI_API_KEY)" ];   then echo "ERROR: AZURE_OPENAI_API_KEY is not set";   exit 1; fi
	@if [ -z "$(EIA_API_KEY)" ];            then echo "WARN: EIA_API_KEY is not set — EIA tool will be disabled"; fi
	@if [ -z "$(ERCOT_API_KEY)" ];          then echo "WARN: ERCOT_API_KEY is not set — ERCOT tool will be disabled"; fi
	@if [ -z "$(SAMGOV_API_KEY)" ];         then echo "WARN: SAMGOV_API_KEY is not set — SAM.gov tool will be disabled"; fi
	@kubectl create secret generic mcp-server-dotnet-secret \
		--namespace $(NAMESPACE) \
		--from-literal=AZURE_SEARCH_ENDPOINT="$(AZURE_SEARCH_ENDPOINT)" \
		--from-literal=AZURE_SEARCH_API_KEY="$(AZURE_SEARCH_API_KEY)" \
		--from-literal=AZURE_OPENAI_ENDPOINT="$(AZURE_OPENAI_ENDPOINT)" \
		--from-literal=AZURE_OPENAI_API_KEY="$(AZURE_OPENAI_API_KEY)" \
		$(if $(EIA_API_KEY),--from-literal=EIA_API_KEY="$(EIA_API_KEY)",) \
		$(if $(ERCOT_API_KEY),--from-literal=ERCOT_API_KEY="$(ERCOT_API_KEY)",) \
		$(if $(SAMGOV_API_KEY),--from-literal=SAMGOV_API_KEY="$(SAMGOV_API_KEY)",) \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ mcp-server-dotnet-secret created in namespace $(NAMESPACE)"

create-agent-api-secret: ## Create agent-api-secret K8s Secret (Azure OpenAI keys + DATABASE_URL + JWT_SECRET + DD_API_KEY/DD_APP_KEY for AI Guard + AZURE_STORAGE_CONNECTION_STRING for chat media uploads)
	@if [ -z "$(AZURE_OPENAI_ENDPOINT)" ]; then echo "ERROR: AZURE_OPENAI_ENDPOINT is not set"; exit 1; fi
	@if [ -z "$(AZURE_OPENAI_API_KEY)" ];  then echo "ERROR: AZURE_OPENAI_API_KEY is not set";  exit 1; fi
	@if [ -z "$(JWT_SECRET)" ]; then echo "ERROR: JWT_SECRET is not set (shared with auth-api for /query auth)"; exit 1; fi
	@if [ -z "$(DATABASE_URL)" ]; then echo "WARN: DATABASE_URL not set — conversation persistence will be disabled"; fi
	@if [ -z "$(DD_API_KEY)" ] || [ -z "$(DD_APP_KEY)" ]; then echo "WARN: DD_API_KEY/DD_APP_KEY not both set — AI Guard LangChain auto-integration will be disabled"; fi
	@if [ -z "$(AZURE_STORAGE_CONNECTION_STRING)" ]; then echo "WARN: AZURE_STORAGE_CONNECTION_STRING not set — chat media upload (image/audio attachments) will be disabled"; fi
	@if [ -z "$(AZURE_OPENAI_WHISPER_ENDPOINT)" ] || [ -z "$(AZURE_OPENAI_WHISPER_API_KEY)" ]; then echo "WARN: AZURE_OPENAI_WHISPER_ENDPOINT/AZURE_OPENAI_WHISPER_API_KEY not both set — voice attachment transcription will be disabled"; fi
	@kubectl create secret generic agent-api-secret \
		--namespace $(NAMESPACE) \
		--from-literal=AZURE_OPENAI_ENDPOINT="$(AZURE_OPENAI_ENDPOINT)" \
		--from-literal=AZURE_OPENAI_API_KEY="$(AZURE_OPENAI_API_KEY)" \
		--from-literal=JWT_SECRET="$(JWT_SECRET)" \
		$(if $(DATABASE_URL),--from-literal=DATABASE_URL="$(DATABASE_URL)",) \
		$(if $(DD_API_KEY),--from-literal=DD_API_KEY="$(DD_API_KEY)",) \
		$(if $(DD_APP_KEY),--from-literal=DD_APP_KEY="$(DD_APP_KEY)",) \
		$(if $(AZURE_STORAGE_CONNECTION_STRING),--from-literal=AZURE_STORAGE_CONNECTION_STRING="$(AZURE_STORAGE_CONNECTION_STRING)",) \
		$(if $(AZURE_OPENAI_WHISPER_ENDPOINT),--from-literal=AZURE_OPENAI_WHISPER_ENDPOINT="$(AZURE_OPENAI_WHISPER_ENDPOINT)",) \
		$(if $(AZURE_OPENAI_WHISPER_API_KEY),--from-literal=AZURE_OPENAI_WHISPER_API_KEY="$(AZURE_OPENAI_WHISPER_API_KEY)",) \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ agent-api-secret created in namespace $(NAMESPACE)"

create-agent-api-dotnet-secret: ## Create agent-api-dotnet-secret K8s Secret (Azure OpenAI keys + DATABASE_URL + DD_API_KEY + DD_APPLICATION_KEY + JWT_SECRET + Whisper keys + AZURE_STORAGE_CONNECTION_STRING for chat media uploads)
	@if [ -z "$(AZURE_OPENAI_ENDPOINT)" ]; then echo "ERROR: AZURE_OPENAI_ENDPOINT is not set"; exit 1; fi
	@if [ -z "$(AZURE_OPENAI_API_KEY)" ];  then echo "ERROR: AZURE_OPENAI_API_KEY is not set";  exit 1; fi
	@if [ -z "$(JWT_SECRET)" ]; then echo "ERROR: JWT_SECRET is not set (shared with auth-api for /query auth)"; exit 1; fi
	@if [ -z "$(DD_API_KEY)" ];            then echo "WARN: DD_API_KEY not set — LLM Observability OTLP export will be disabled"; fi
	@if [ -z "$(DD_APPLICATION_KEY)" ];    then echo "WARN: DD_APPLICATION_KEY not set — AI Guard HTTP API calls will be disabled"; fi
	@if [ -z "$(DATABASE_URL)" ]; then echo "WARN: DATABASE_URL not set — conversation persistence will be disabled"; fi
	@if [ -z "$(AZURE_STORAGE_CONNECTION_STRING)" ]; then echo "WARN: AZURE_STORAGE_CONNECTION_STRING not set — chat media upload (image/audio attachments) will be disabled"; fi
	@if [ -z "$(AZURE_OPENAI_WHISPER_ENDPOINT)" ] || [ -z "$(AZURE_OPENAI_WHISPER_API_KEY)" ]; then echo "WARN: AZURE_OPENAI_WHISPER_ENDPOINT/AZURE_OPENAI_WHISPER_API_KEY not both set — voice attachment transcription will be disabled"; fi
	@kubectl create secret generic agent-api-dotnet-secret \
		--namespace $(NAMESPACE) \
		--from-literal=AZURE_OPENAI_ENDPOINT="$(AZURE_OPENAI_ENDPOINT)" \
		--from-literal=AZURE_OPENAI_API_KEY="$(AZURE_OPENAI_API_KEY)" \
		--from-literal=JWT_SECRET="$(JWT_SECRET)" \
		$(if $(DD_API_KEY),--from-literal=DD_API_KEY="$(DD_API_KEY)",) \
		$(if $(DD_APPLICATION_KEY),--from-literal=DD_APPLICATION_KEY="$(DD_APPLICATION_KEY)",) \
		$(if $(DATABASE_URL),--from-literal=DATABASE_URL="$(DATABASE_URL)",) \
		$(if $(AZURE_STORAGE_CONNECTION_STRING),--from-literal=AZURE_STORAGE_CONNECTION_STRING="$(AZURE_STORAGE_CONNECTION_STRING)",) \
		$(if $(AZURE_OPENAI_WHISPER_ENDPOINT),--from-literal=AZURE_OPENAI_WHISPER_ENDPOINT="$(AZURE_OPENAI_WHISPER_ENDPOINT)",) \
		$(if $(AZURE_OPENAI_WHISPER_API_KEY),--from-literal=AZURE_OPENAI_WHISPER_API_KEY="$(AZURE_OPENAI_WHISPER_API_KEY)",) \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ agent-api-dotnet-secret created in namespace $(NAMESPACE)"

create-load-generator-secret: ## Create load-generator-secret K8s Secret (Datadog API key)
	@if [ -z "$(DD_API_KEY)" ]; then echo "ERROR: DD_API_KEY is not set"; exit 1; fi
	@kubectl create secret generic load-generator-secret \
		--namespace $(NAMESPACE) \
		--from-literal=DD_API_KEY="$(DD_API_KEY)" \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ load-generator-secret created in namespace $(NAMESPACE)"

create-redis-secret: ## Create redis-secret K8s Secret (REDIS_PASSWORD)
	@if [ -z "$(REDIS_PASSWORD)" ]; then echo "ERROR: REDIS_PASSWORD is not set — generate with: openssl rand -base64 24"; exit 1; fi
	@kubectl create secret generic redis-secret \
		--namespace $(NAMESPACE) \
		--from-literal=REDIS_PASSWORD="$(REDIS_PASSWORD)" \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ redis-secret created"

create-postgres-secret: ## Create postgres-secret K8s Secret
	@if [ -z "$(POSTGRES_USER)" ]; then echo "ERROR: POSTGRES_USER is not set"; exit 1; fi
	@if [ -z "$(POSTGRES_PASSWORD)" ]; then echo "ERROR: POSTGRES_PASSWORD is not set"; exit 1; fi
	@if [ -z "$(POSTGRES_DB)" ]; then echo "ERROR: POSTGRES_DB is not set"; exit 1; fi
	@kubectl create secret generic postgres-secret \
		--namespace $(NAMESPACE) \
		--from-literal=POSTGRES_USER="$(POSTGRES_USER)" \
		--from-literal=POSTGRES_PASSWORD="$(POSTGRES_PASSWORD)" \
		--from-literal=POSTGRES_DB="$(POSTGRES_DB)" \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ postgres-secret created"

create-auth-api-secret: ## Create auth-api-secret K8s Secret (DATABASE_URL, JWT_SECRET, optional bootstrap admin)
	@if [ -z "$(DATABASE_URL)" ]; then echo "ERROR: DATABASE_URL is not set"; exit 1; fi
	@if [ -z "$(JWT_SECRET)" ]; then echo "ERROR: JWT_SECRET is not set"; exit 1; fi
	@if [ -z "$(BOOTSTRAP_ADMIN_EMAIL)" ] || [ -z "$(BOOTSTRAP_ADMIN_PASSWORD)" ]; then \
		echo "WARN: BOOTSTRAP_ADMIN_EMAIL/PASSWORD not set — auth-api will start without a bootstrap admin"; \
		echo "      (existing admin users keep working; only matters on a fresh DB)"; \
	fi
	@kubectl create secret generic auth-api-secret \
		--namespace $(NAMESPACE) \
		--from-literal=DATABASE_URL="$(DATABASE_URL)" \
		--from-literal=JWT_SECRET="$(JWT_SECRET)" \
		$(if $(BOOTSTRAP_ADMIN_EMAIL),--from-literal=BOOTSTRAP_ADMIN_EMAIL="$(BOOTSTRAP_ADMIN_EMAIL)",) \
		$(if $(BOOTSTRAP_ADMIN_PASSWORD),--from-literal=BOOTSTRAP_ADMIN_PASSWORD="$(BOOTSTRAP_ADMIN_PASSWORD)",) \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ auth-api-secret created"

create-dd-postgres-secret: ## Create dd-postgres-secret K8s Secret in datadog namespace (referenced by DatadogAgent CR)
	@if [ -z "$(DD_POSTGRES_PASSWORD)" ]; then echo "ERROR: DD_POSTGRES_PASSWORD is not set"; exit 1; fi
	@kubectl create secret generic dd-postgres-secret \
		--namespace datadog \
		--from-literal=DD_POSTGRES_PASSWORD="$(DD_POSTGRES_PASSWORD)" \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ dd-postgres-secret created in namespace datadog"

create-mailpit-secret: ## Create mailpit-secret with bcrypt-hashed MP_UI_AUTH for the inbox web UI
	@if [ -z "$(MAILPIT_UI_USERNAME)" ]; then echo "ERROR: MAILPIT_UI_USERNAME is not set"; exit 1; fi
	@if [ -z "$(MAILPIT_UI_PASSWORD)" ]; then echo "ERROR: MAILPIT_UI_PASSWORD is not set — generate one with: openssl rand -base64 24"; exit 1; fi
	@command -v htpasswd >/dev/null 2>&1 || { echo "ERROR: htpasswd not found — install apache2-utils (Debian) or httpd (macOS)"; exit 1; }
	@# `htpasswd -nbB` emits user:$2y$10$... — Mailpit's MP_UI_AUTH accepts the
	@# same bcrypt prefix variants ($2a / $2b / $2y), so no rewriting needed.
	@AUTH="$$(htpasswd -nbB -C 10 "$(MAILPIT_UI_USERNAME)" "$(MAILPIT_UI_PASSWORD)")"; \
	@kubectl create secret generic mailpit-secret \
		--namespace $(NAMESPACE) \
		--from-literal=MP_UI_AUTH="$$AUTH" \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ mailpit-secret created (user: $(MAILPIT_UI_USERNAME))"

create-secrets: create-mcp-server-secret create-mcp-server-dotnet-secret create-agent-api-secret create-agent-api-dotnet-secret create-load-generator-secret create-redis-secret create-postgres-secret create-auth-api-secret create-dd-postgres-secret create-airflow-secret create-mailpit-secret ## Create all application K8s secrets

redeploy-mailpit: ## Apply the Mailpit manifest, evict stuck pods from older ReplicaSets, wait for rollout, verify probe + endpoint
	@echo "→ Applying k8s/mailpit/deployment.yaml + service.yaml + configmap.yaml..."
	kubectl apply -f k8s/mailpit/
	@echo "→ Force-deleting any pods from older ReplicaSets so the new RS can roll fresh..."
	@# --force --grace-period=0 because stuck CrashLoopBackOff pods otherwise
	@# block the rollout when terminationGracePeriodSeconds is the default 30s
	@# and the kubelet is still bouncing them.
	kubectl delete pod -n $(NAMESPACE) -l app=mailpit --force --grace-period=0 2>/dev/null || true
	@echo "→ Waiting for rollout to reach Ready..."
	kubectl rollout status deploy/mailpit -n $(NAMESPACE) --timeout=2m
	@echo "→ Verifying the live readiness probe is tcpSocket (not httpGet)..."
	@PROBE=$$(kubectl get deploy mailpit -n $(NAMESPACE) -o jsonpath='{.spec.template.spec.containers[0].readinessProbe}'); \
	if echo "$$PROBE" | grep -q tcpSocket; then \
		echo "  ✓ readiness probe is tcpSocket"; \
	else \
		echo "  ✗ readiness probe is NOT tcpSocket — apply did not take effect"; \
		echo "    spec: $$PROBE"; \
		exit 1; \
	fi
	@echo "→ Verifying the Service has a healthy endpoint..."
	@EP=$$(kubectl get endpoints mailpit -n $(NAMESPACE) -o jsonpath='{.subsets[*].addresses[*].ip}'); \
	if [ -n "$$EP" ]; then \
		echo "  ✓ mailpit endpoint(s): $$EP"; \
	else \
		echo "  ✗ mailpit endpoint is empty — pod still not Ready"; \
		exit 1; \
	fi
	@echo "✓ Mailpit redeployed. Open https://infra-advisor-ai.kyletaylor.dev/mailpit/ (basic auth: $(MAILPIT_UI_USERNAME))"

setup-postgres-dbm: ## Create Datadog monitoring user + grants in Postgres (run once after deploy; requires authuser superuser)
	@if [ -z "$(DD_POSTGRES_PASSWORD)" ]; then echo "ERROR: DD_POSTGRES_PASSWORD is not set"; exit 1; fi
	chmod +x k8s/postgres/setup-dbm.sh
	NAMESPACE=$(NAMESPACE) \
		POSTGRES_USER=$${POSTGRES_USER:-authuser} \
		POSTGRES_DB=$${POSTGRES_DB:-postgres} \
		DD_POSTGRES_PASSWORD='$(DD_POSTGRES_PASSWORD)' \
		bash k8s/postgres/setup-dbm.sh

create-ghcr-secret: ## Create ghcr-pull-secret K8s Secret in infra-advisor namespace
	@if [ -z "$(GHCR_PAT)" ]; then echo "ERROR: GHCR_PAT is not set"; exit 1; fi
	@if [ -z "$(GITHUB_EMAIL)" ]; then echo "ERROR: GITHUB_EMAIL is not set"; exit 1; fi
	@kubectl create secret docker-registry ghcr-pull-secret \
		--namespace $(NAMESPACE) \
		--docker-server=ghcr.io \
		--docker-username=kyletaylored \
		--docker-password=$(GHCR_PAT) \
		--docker-email=$(GITHUB_EMAIL) \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ ghcr-pull-secret created in namespace $(NAMESPACE)"

create-airflow-ghcr-secret: ## Create ghcr-pull-secret K8s Secret in the Airflow namespace
	@if [ -z "$(GHCR_PAT)" ]; then echo "ERROR: GHCR_PAT is not set"; exit 1; fi
	@if [ -z "$(GITHUB_EMAIL)" ]; then echo "ERROR: GITHUB_EMAIL is not set"; exit 1; fi
	@kubectl create namespace $(AIRFLOW_NAMESPACE) --dry-run=client -o yaml | kubectl apply -f -
	@kubectl create secret docker-registry ghcr-pull-secret \
		--namespace $(AIRFLOW_NAMESPACE) \
		--docker-server=ghcr.io \
		--docker-username=kyletaylored \
		--docker-password=$(GHCR_PAT) \
		--docker-email=$(GITHUB_EMAIL) \
		--dry-run=client -o yaml | kubectl apply -f -
	@echo "✓ ghcr-pull-secret created in namespace $(AIRFLOW_NAMESPACE)"

deploy-k8s: check-env ## Apply all Kubernetes manifests
	@echo "→ Applying namespaces..."
	kubectl apply -f k8s/namespace.yaml

	@echo "→ Installing Strimzi CRDs..."
	kubectl apply -f https://strimzi.io/install/latest?namespace=kafka || true
	@echo "  Waiting for Strimzi CRDs to be established..."
	kubectl wait --for=condition=established crd/kafkas.kafka.strimzi.io --timeout=90s
	kubectl wait --for=condition=established crd/kafkatopics.kafka.strimzi.io --timeout=30s

	@echo "→ Skipping k8s/datadog/ — Datadog deployed via Operator (datadog/datadog-agent.yaml)"

	@echo "→ Deploying Kafka (Strimzi)..."
	kubectl apply -f k8s/kafka/

	@echo "→ Deploying Redis..."
	kubectl apply -f k8s/redis/

	@echo "→ Deploying Mailpit (SMTP capture for dev)..."
	$(MAKE) create-mailpit-secret
	kubectl apply -f k8s/mailpit/

	@echo "→ Creating Airflow Azure secret..."
	$(MAKE) create-airflow-secret
	@echo "→ Creating Airflow GHCR pull secret..."
	$(MAKE) create-airflow-ghcr-secret

	@echo "→ Deploying Airflow..."
	helm repo add apache-airflow https://airflow.apache.org || true
	helm repo update
	@if helm status airflow -n $(AIRFLOW_NAMESPACE) >/dev/null 2>&1; then \
		$(MAKE) upgrade-airflow; \
	else \
		$(MAKE) install-airflow; \
	fi

	@echo "→ Creating GHCR pull secret..."
	$(MAKE) create-ghcr-secret

	@echo "→ Creating application secrets..."
	$(MAKE) create-mcp-server-secret
	$(MAKE) create-mcp-server-dotnet-secret
	$(MAKE) create-agent-api-secret
	$(MAKE) create-agent-api-dotnet-secret
	$(MAKE) create-load-generator-secret
	$(MAKE) create-postgres-secret
	$(MAKE) create-auth-api-secret
	$(MAKE) create-dd-postgres-secret

	@echo "→ Deploying Postgres..."
	kubectl apply -f k8s/postgres/

	@echo "→ Deploying application services..."
	kubectl apply -f k8s/mcp-server/
	kubectl apply -f k8s/mcp-server-dotnet/
	kubectl apply -f k8s/agent-api/
	kubectl apply -f k8s/agent-api-dotnet/
	kubectl apply -f k8s/auth-api/
	kubectl apply -f k8s/load-generator/
	kubectl apply -f k8s/ui/

	@echo "✓ All Kubernetes resources applied"

rollout-status: ## Check rollout status for all infra-advisor deployments
	kubectl rollout status deploy/mcp-server -n $(NAMESPACE) --timeout=5m &
	kubectl rollout status deploy/agent-api -n $(NAMESPACE) --timeout=5m &
	wait
	@echo "✓ All deployments ready"

# ─── Airflow DAGs ─────────────────────────────────────────────────────────────

run-dags: ## Manually trigger the selected Airflow canary DAGs
	@echo "→ Triggering knowledge_base_init DAG..."
	kubectl exec -n airflow airflow-scheduler-0 -c scheduler -- airflow dags trigger knowledge_base_init
	@echo "→ Triggering nbi_refresh DAG..."
	kubectl exec -n airflow airflow-scheduler-0 -c scheduler -- airflow dags trigger nbi_refresh
	@echo "→ Triggering fema_refresh DAG..."
	kubectl exec -n airflow airflow-scheduler-0 -c scheduler -- airflow dags trigger fema_refresh
	@echo "→ Triggering eia_refresh DAG..."
	kubectl exec -n airflow airflow-scheduler-0 -c scheduler -- airflow dags trigger eia_refresh
	@echo "→ Triggering twdb_water_plan_refresh DAG..."
	kubectl exec -n airflow airflow-scheduler-0 -c scheduler -- airflow dags trigger twdb_water_plan_refresh
	@echo "✓ All DAGs triggered — check Airflow UI at https://infra-advisor-ai.kyletaylor.dev/airflow"

airflow-ui: ## Port-forward Airflow web UI to localhost:8080
	kubectl port-forward -n airflow svc/airflow-api-server 8080:8080

apply-datadog-agent: ## Apply DatadogAgent CR from datadog/datadog-agent.yaml
	kubectl apply -f datadog/datadog-agent.yaml
	@echo "✓ DatadogAgent CR applied"

install-airflow: verify-airflow-image ## Install Airflow only when the Helm release does not already exist
	@if [ -z "$(AIRFLOW_ADMIN_USERNAME)" ]; then echo "ERROR: AIRFLOW_ADMIN_USERNAME is not set (override Airflow admin user)"; exit 1; fi
	@if [ -z "$(AIRFLOW_ADMIN_PASSWORD)" ]; then echo "ERROR: AIRFLOW_ADMIN_PASSWORD is not set — generate one with: openssl rand -base64 32"; exit 1; fi
	@if helm status airflow -n $(AIRFLOW_NAMESPACE) >/dev/null 2>&1; then echo "ERROR: Airflow already exists; use make upgrade-airflow for a non-destructive rollout"; exit 1; fi
	helm repo add apache-airflow https://airflow.apache.org || true
	helm repo update
	@kubectl create namespace $(AIRFLOW_NAMESPACE) --dry-run=client -o yaml | kubectl apply -f -
	$(MAKE) create-airflow-ghcr-secret
	$(MAKE) create-airflow-secret
	@echo "→ Installing Airflow and waiting for migration jobs..."
	helm install airflow apache-airflow/airflow \
		--version $(AIRFLOW_CHART_VERSION) \
		--namespace $(AIRFLOW_NAMESPACE) \
		--values k8s/airflow/values.yaml \
		--set images.airflow.repository='$(AIRFLOW_IMAGE_REPOSITORY)' \
		--set images.airflow.tag='$(AIRFLOW_IMAGE_TAG)' \
		--set createUserJob.defaultUser.username='$(AIRFLOW_ADMIN_USERNAME)' \
		--set createUserJob.defaultUser.password='$(AIRFLOW_ADMIN_PASSWORD)' \
		--timeout 20m \
		--wait \
		--wait-for-jobs \
		--atomic
	@echo "✓ Airflow installed and ready"

recover-airflow-destructive: ## Delete and reinstall Airflow only after explicit data-loss acknowledgement
	@if [ "$(AIRFLOW_DESTRUCTIVE_RECOVERY)" != "delete-airflow-release-and-namespace" ]; then \
		echo "ERROR: destructive recovery is disabled"; \
		echo "After backing up metadata and logs, rerun with AIRFLOW_DESTRUCTIVE_RECOVERY=delete-airflow-release-and-namespace"; \
		exit 1; \
	fi
	helm uninstall airflow -n $(AIRFLOW_NAMESPACE) --no-hooks
	kubectl delete namespace $(AIRFLOW_NAMESPACE) --wait=true
	$(MAKE) install-airflow

preflight-airflow-cluster: ## Verify the current Airflow release is coherent before an upgrade
	EXPECTED_AIRFLOW_IMAGE="$(EXPECTED_AIRFLOW_IMAGE)" services/ingestion/scripts/cluster_preflight.sh

verify-airflow-image: ## Pull and verify the exact Airflow image before a Helm install or upgrade
	docker pull '$(AIRFLOW_IMAGE_REPOSITORY):$(AIRFLOW_IMAGE_TAG)'
	docker run --rm \
		--env DD_TRACE_ENABLED=false \
		'$(AIRFLOW_IMAGE_REPOSITORY):$(AIRFLOW_IMAGE_TAG)' \
		python /opt/airflow/scripts/verify_image_contract.py

upgrade-airflow: preflight-airflow-cluster create-airflow-ghcr-secret verify-airflow-image ## Upgrade a healthy Airflow Helm release from k8s/airflow/values.yaml
	helm repo add apache-airflow https://airflow.apache.org || true
	helm repo update
	helm upgrade airflow apache-airflow/airflow \
		--version $(AIRFLOW_CHART_VERSION) \
		--namespace $(AIRFLOW_NAMESPACE) \
		--values k8s/airflow/values.yaml \
		--set images.airflow.repository='$(AIRFLOW_IMAGE_REPOSITORY)' \
		--set images.airflow.tag='$(AIRFLOW_IMAGE_TAG)' \
		--set createUserJob.enabled=false \
		--timeout 20m \
		--wait \
		--wait-for-jobs \
		--cleanup-on-fail \
		--atomic
	EXPECTED_AIRFLOW_IMAGE="$(AIRFLOW_IMAGE_REPOSITORY):$(AIRFLOW_IMAGE_TAG)" services/ingestion/scripts/cluster_preflight.sh
	@echo "✓ Airflow upgraded"

sync-dags: ## Explain immutable DAG delivery (kept for backwards compatibility)
	@echo "DAGs and helper scripts are bundled in $(AIRFLOW_IMAGE_REPOSITORY):$(AIRFLOW_IMAGE_TAG)."
	@echo "Build the image, push it, then run make upgrade-airflow AIRFLOW_IMAGE_TAG=<immutable-tag>."

build-airflow-image: ## Build the pinned local Airflow ingestion image
	docker build --pull \
		--tag infra-advisor-airflow:test \
		services/ingestion

test-airflow: ## Run ingestion unit tests and the real Airflow DAG import contract
	cd services/ingestion && DD_TRACE_ENABLED=false uv run --frozen --all-extras pytest -x tests/
	cd services/ingestion && uv run --frozen python scripts/verify_image_contract.py

test-airflow-container: build-airflow-image ## Verify packages, scripts, and DAG imports inside the image
	docker run --rm \
		--env DD_TRACE_ENABLED=false \
		infra-advisor-airflow:test \
		python /opt/airflow/scripts/verify_image_contract.py

# ─── Tests ────────────────────────────────────────────────────────────────────

test-mcp: ## Run MCP server tests
	uv run pytest -x services/mcp-server/tests/

test-agent: ## Run agent API tests
	uv run pytest -x services/agent-api/tests/

test-load-gen: ## Run load generator tests
	uv run pytest -x services/load-generator/tests/

test-all: test-mcp test-agent test-load-gen ## Run all service tests

# ─── Docker ───────────────────────────────────────────────────────────────────

GHCR_PREFIX ?= ghcr.io/kyletaylored/infra-advisor-ai
IMAGE_TAG ?= $(shell git rev-parse --short HEAD 2>/dev/null || echo "local")

docker-build-mcp: ## Build MCP server image
	docker build -t $(GHCR_PREFIX)/mcp-server:$(IMAGE_TAG) services/mcp-server/

docker-build-agent: ## Build agent API image
	docker build -t $(GHCR_PREFIX)/agent-api:$(IMAGE_TAG) services/agent-api/

docker-build-load-gen: ## Build load generator image
	docker build -t $(GHCR_PREFIX)/load-generator:$(IMAGE_TAG) services/load-generator/

docker-build-ui: ## Build UI image
	docker build -t $(GHCR_PREFIX)/ui:$(IMAGE_TAG) services/ui/

docker-build-all: docker-build-mcp docker-build-agent docker-build-load-gen docker-build-ui ## Build all images

docker-push-all: ## Push all images to GHCR
	docker push $(GHCR_PREFIX)/mcp-server:$(IMAGE_TAG)
	docker push $(GHCR_PREFIX)/agent-api:$(IMAGE_TAG)
	docker push $(GHCR_PREFIX)/load-generator:$(IMAGE_TAG)
	docker push $(GHCR_PREFIX)/ui:$(IMAGE_TAG)

# ─── Verification ─────────────────────────────────────────────────────────────

check-pods: ## Check pod status across all namespaces
	@echo "=== infra-advisor ==="
	kubectl get pods -n infra-advisor
	@echo ""
	@echo "=== kafka ==="
	kubectl get pods -n kafka
	@echo ""
	@echo "=== airflow ==="
	kubectl get pods -n airflow
	@echo ""
	@echo "=== datadog ==="
	kubectl get pods -n datadog

check-nodes: ## Check AKS node status
	kubectl get nodes -o wide

logs-mcp: ## Tail MCP server logs
	kubectl logs -n $(NAMESPACE) deploy/mcp-server --tail=50 -f

logs-agent: ## Tail agent API logs
	kubectl logs -n $(NAMESPACE) deploy/agent-api --tail=50 -f

# ─── Experiments ──────────────────────────────────────────────────────────────

# Default port 5005 — macOS AirPlay Receiver hijacks :5000. Override with
# `make run-otel-poc OTEL_POC_PORT=7000` if 5005 is also in use.
OTEL_POC_PORT ?= 5005

otel-poc: ## Start collector + run POC (single entry point — Ctrl+C stops both)
	@$(MAKE) --no-print-directory start-otel-collector
	@echo ""
	@echo "▸ POC starting on http://localhost:$(OTEL_POC_PORT)"
	@echo "  Ctrl+C will stop the POC AND tear down the collector."
	@echo ""
	@# Trap fires on Ctrl+C (INT), kill (TERM), or normal/error exit (EXIT).
	@# Always runs `stop-otel-collector` so we never leave the container
	@# running when the foreground POC exits.
	@trap '$(MAKE) --no-print-directory stop-otel-collector' EXIT INT TERM; \
	$(MAKE) --no-print-directory run-otel-poc

run-otel-poc: ## Run the .NET OTel POC only (assumes collector already running)
	@# Shell-level $$VAR (not Make's $(VAR)) so secrets aren't expanded into
	@# the recipe text at parse time — keeps them out of `make -n` output.
	@if [ -z "$$AZURE_OPENAI_ENDPOINT" ] || [ -z "$$AZURE_OPENAI_API_KEY" ]; then \
		echo "ERROR: AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY must be set in root .env"; \
		exit 1; \
	fi
	@# The POC defaults to OTLP → http://localhost:4318 (local OTel Collector).
	@# Warn if nothing is listening there. Override OTEL_EXPORTER_OTLP_ENDPOINT
	@# to point at any other OTLP-compatible endpoint instead.
	@if [ -z "$$OTEL_EXPORTER_OTLP_ENDPOINT" ] && ! nc -z localhost 4318 2>/dev/null; then \
		echo "WARN: Nothing listening on localhost:4318 — telemetry will fail to export."; \
		echo "      Run `make start-otel-collector` first, OR set"; \
		echo "      OTEL_EXPORTER_OTLP_ENDPOINT to point at another OTLP endpoint."; \
		echo ""; \
	fi
	@echo "→ Starting .NET OTel POC on http://localhost:$(OTEL_POC_PORT)  (Ctrl+C to stop)"
	@echo "  OTLP target: $${OTEL_EXPORTER_OTLP_ENDPOINT:-http://localhost:4318}"
	@echo "  Service: $${OTEL_SERVICE_NAME:-otel-genai-poc}"
	@echo ""
	@# RUM env passthrough: the main UI's .env uses VITE_DD_RUM_APP_ID /
	@# VITE_DD_RUM_CLIENT_TOKEN. Map those onto the POC's expected
	@# DD_RUM_APPLICATION_ID / DD_RUM_CLIENT_TOKEN if the POC-prefixed
	@# names aren't explicitly set.
	@cd experiments/dotnet-otel-poc && \
		ASPNETCORE_URLS=http://localhost:$(OTEL_POC_PORT) \
		DD_RUM_APPLICATION_ID="$${DD_RUM_APPLICATION_ID:-$$VITE_DD_RUM_APP_ID}" \
		DD_RUM_CLIENT_TOKEN="$${DD_RUM_CLIENT_TOKEN:-$$VITE_DD_RUM_CLIENT_TOKEN}" \
		DD_SITE="$${DD_SITE:-$$VITE_DD_RUM_SITE}" \
		dotnet run

build-otel-poc: ## Build the .NET OTel POC without running (compile-check only)
	cd experiments/dotnet-otel-poc && dotnet build -c Release

# ─── MAF POC (Microsoft Agents Framework) ──────────────────────────────────────
# Mirrors the M.E.AI-only POC but on Microsoft.Agents.AI 1.5.0 — adds the
# invoke_agent span layer, AgentSession-based conversation grouping, and
# the AIContextProvider hook. Listens on a separate port (5007) so both
# POCs can run side-by-side against the same local collector.

OTEL_MAF_POC_PORT ?= 5007

otel-maf-poc: ## Start collector + run MAF POC (Ctrl+C stops both)
	@$(MAKE) --no-print-directory start-otel-collector
	@echo ""
	@echo "▸ MAF POC starting on http://localhost:$(OTEL_MAF_POC_PORT)  (Ctrl+C to stop)"
	@echo ""
	@trap '$(MAKE) --no-print-directory stop-otel-collector' EXIT INT TERM; \
	$(MAKE) --no-print-directory run-otel-maf-poc

run-otel-maf-poc: ## Run the MAF POC only (assumes collector already running)
	@if [ -z "$$AZURE_OPENAI_ENDPOINT" ] || [ -z "$$AZURE_OPENAI_API_KEY" ]; then \
		echo "ERROR: AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY must be set in root .env"; \
		exit 1; \
	fi
	@if [ -z "$$OTEL_EXPORTER_OTLP_ENDPOINT" ] && ! nc -z localhost 4318 2>/dev/null; then \
		echo "WARN: Nothing listening on localhost:4318 — telemetry will fail to export."; \
		echo "      Run `make start-otel-collector` first."; \
		echo ""; \
	fi
	@echo "→ Starting MAF POC on http://localhost:$(OTEL_MAF_POC_PORT)  (Ctrl+C to stop)"
	@echo "  OTLP target: $${OTEL_EXPORTER_OTLP_ENDPOINT:-http://localhost:4318}"
	@echo "  Service: $${OTEL_SERVICE_NAME:-infra-advisor-maf-poc}"
	@echo ""
	@# Hardcode MCP_SERVER_URL to the port-forwarded localhost — root .env
	@# has the cluster-internal MCP_SERVER_URL (used by the production
	@# agent-api), which isn't resolvable from the host. Override via
	@# MAF_POC_MCP_URL=... in your shell to point at any other target.
	@cd experiments/dotnet-maf-poc && \
		ASPNETCORE_URLS=http://localhost:$(OTEL_MAF_POC_PORT) \
		MCP_SERVER_URL="$${MAF_POC_MCP_URL:-http://localhost:8000/mcp}" \
		DD_RUM_APPLICATION_ID="$${DD_RUM_APPLICATION_ID:-$$VITE_DD_RUM_APP_ID}" \
		DD_RUM_CLIENT_TOKEN="$${DD_RUM_CLIENT_TOKEN:-$$VITE_DD_RUM_CLIENT_TOKEN}" \
		DD_SITE="$${DD_SITE:-$$VITE_DD_RUM_SITE}" \
		dotnet run

build-otel-maf-poc: ## Build the MAF POC without running (compile-check only)
	cd experiments/dotnet-maf-poc && dotnet build -c Release

start-otel-collector: ## Start local OTel Collector (Docker) on :4317 / :4318
	@if [ -z "$$DD_API_KEY" ]; then \
		echo "ERROR: DD_API_KEY must be set (root .env)"; exit 1; \
	fi
	cd experiments/otel-collector && docker compose up -d
	@echo ""
	@echo "✓ Collector running. Tail logs:  make logs-otel-collector"
	@echo "                     Stop it:     make stop-otel-collector"

stop-otel-collector: ## Stop local OTel Collector
	cd experiments/otel-collector && docker compose down

logs-otel-collector: ## Tail local OTel Collector logs (every span/metric/log body)
	docker logs -f otel-collector-poc
