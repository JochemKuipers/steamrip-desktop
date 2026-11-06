# SteamRIP Desktop — everyday commands. `make` or `make help` lists them.
# Every target is a thin wrapper over dotnet/git so nothing here is required; it just saves typing.

SHELL        := /bin/sh
APP          := src/SteamRip.App
SLN          := SteamRip.slnx
CONFIG       ?= Debug
RIDS         := linux-x64 win-x64
OUT          := out
# Throwaway profile for development runs: never touches the real library/DB under ~/.config.
DEV_HOME     ?= /tmp/sr-cfg
DEV_LOGS     := $(DEV_HOME)/steamrip-desktop/logs
DEV_DB       := $(DEV_HOME)/steamrip-desktop/steamrip.db

.DEFAULT_GOAL := help
.PHONY: help setup tools hooks restore build rebuild test run run-dev run-hidden watch \
        format format-check lint inspect inspect-all inspect-html check ci \
        publish publish-linux publish-win package package-win release \
        clean distclean logs log-tail db dev-reset

## ---- setup ---------------------------------------------------------------------------------

help: ## Show this help
	@awk 'BEGIN {FS = ":.*## "; printf "\nUsage: make <target>\n\n"} \
	     /^## ----/ {gsub(/^## -+ */, ""); gsub(/ -+$$/, ""); printf "\n\033[1m%s\033[0m\n", $$0; next} \
	     /^[a-zA-Z_-]+:.*## / {printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}' $(MAKEFILE_LIST)
	@echo

setup: tools hooks restore ## First-time setup for a fresh clone (tools, git hooks, NuGet restore)

tools: ## Restore local dotnet tools (CSharpier, ReSharper CLI) from dotnet-tools.json
	dotnet tool restore

hooks: ## Point git at .githooks (pre-commit: format+build, pre-push: full gate)
	git config core.hooksPath .githooks
	@echo "hooks enabled: $$(git config core.hooksPath)"

restore: ## NuGet restore
	dotnet restore $(SLN)

## ---- build & run ---------------------------------------------------------------------------

build: ## Build the solution (analyzers on, warnings are errors)
	dotnet build $(SLN) -c $(CONFIG) --nologo

rebuild: ## Clean build, so every analyzer runs on every file
	dotnet build $(SLN) -c $(CONFIG) --nologo --no-incremental

test: ## Run the unit tests
	dotnet test $(SLN) -c $(CONFIG) --nologo

run: ## Run the app against the real profile (~/.config/steamrip-desktop), window shown
	dotnet run --project $(APP) -c $(CONFIG) -- --show

run-dev: ## Run the app against the throwaway profile in $(DEV_HOME) with verbose logging
	@mkdir -p $(DEV_HOME)
	XDG_CONFIG_HOME=$(DEV_HOME) dotnet run --project $(APP) -c $(CONFIG) -- --show --verbose

run-hidden: ## Run the app tray-only, as autostart would
	dotnet run --project $(APP) -c $(CONFIG) -- --hidden

watch: ## Rebuild and restart the app on source changes (dev profile)
	XDG_CONFIG_HOME=$(DEV_HOME) dotnet watch --project $(APP) run -- --show --verbose

## ---- quality -------------------------------------------------------------------------------

format: ## Format all C# and AXAML with CSharpier
	dotnet csharpier format .

format-check: ## Fail if anything is not CSharpier-formatted
	dotnet csharpier check .

lint: rebuild ## Roslyn analyzers + .editorconfig style rules (that is just a clean build)

inspect: ## ReSharper inspections at WARNING and above; fails on any finding (what pre-push and CI run)
	scripts/inspect.sh

inspect-all: ## ReSharper at the highest level: every HINT, SUGGESTION, WARNING and ERROR, never fails
	SEVERITY=HINT FAIL=0 scripts/inspect.sh obj/inspectcode-all.txt

inspect-html: ## ReSharper full report as HTML (obj/inspectcode.html)
	SEVERITY=HINT FORMAT=Html scripts/inspect.sh
	@command -v xdg-open >/dev/null && xdg-open obj/inspectcode.html || true

check: format-check rebuild test inspect ## All gates, same as the pre-push hook

ci: check ## Alias for what CI runs

## ---- publish ------------------------------------------------------------------------------

publish: $(addprefix publish-,$(RIDS)) ## Self-contained single-file builds for every RID into $(OUT)/

publish-linux: ## Publish linux-x64 to $(OUT)/linux-x64
	dotnet publish $(APP) -c Release -r linux-x64 -o $(OUT)/linux-x64 --nologo

publish-win: ## Publish win-x64 to $(OUT)/win-x64
	dotnet publish $(APP) -c Release -r win-x64 -o $(OUT)/win-x64 --nologo

publish-%:
	dotnet publish $(APP) -c Release -r $* -o $(OUT)/$* --nologo

## ---- release -------------------------------------------------------------------------------

package: ## Linux packages into dist/: .deb, .rpm, Arch .pkg.tar.zst, AppImage (nfpm + appimagetool are fetched into obj/tools)
	scripts/package.sh

package-win: ## Windows installer into dist/ (needs Windows + Inno Setup 6; CI does this on a windows runner)
	pwsh scripts/package-windows.ps1

release: ## Tag and push a release: make release VERSION=1.2.3 (clean tree + full gate first; GitHub Actions does the rest)
	@test -n "$(VERSION)" || { echo "usage: make release VERSION=1.2.3"; exit 2; }
	@echo "$(VERSION)" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$$' || { echo "VERSION must be semver, e.g. 1.2.3 or 1.2.3-rc1"; exit 2; }
	@test -z "$$(git status --porcelain)" || { echo "working tree is not clean"; exit 2; }
	@! git rev-parse -q --verify "refs/tags/v$(VERSION)" >/dev/null || { echo "tag v$(VERSION) already exists"; exit 2; }
	$(MAKE) check
	git tag -a "v$(VERSION)" -m "v$(VERSION)"
	git push origin HEAD "v$(VERSION)"
	@echo "Pushed v$(VERSION); the Release workflow will attach the packages: https://github.com/JochemKuipers/steamrip-desktop/releases/tag/v$(VERSION)"

## ---- housekeeping --------------------------------------------------------------------------

clean: ## Remove build output
	dotnet clean $(SLN) --nologo -v quiet
	rm -rf $(OUT) dist obj/inspectcode* obj/jbcache

distclean: clean ## Also remove every bin/ and obj/
	find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

logs: ## Show today's dev-profile log
	@ls -t $(DEV_LOGS)/*.log 2>/dev/null | head -1 | xargs -r cat

log-tail: ## Follow the dev-profile log
	@f=$$(ls -t $(DEV_LOGS)/*.log 2>/dev/null | head -1); [ -n "$$f" ] && tail -f "$$f" || echo "no log yet under $(DEV_LOGS)"

db: ## Open the dev-profile SQLite database
	sqlite3 $(DEV_DB)

dev-reset: ## Wipe the throwaway dev profile (settings, DB, logs, covers); the real profile is untouched
	rm -rf $(DEV_HOME)/steamrip-desktop
