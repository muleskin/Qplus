# Qplus query server — build, install and service management.
#
#   make            list the targets
#   make publish    build a single self-contained binary into ./out
#   sudo make install   install it, with a systemd unit and a data directory
#
# Override anything on the command line, e.g.
#   make publish RID=linux-arm64
#   sudo make install PORT=8080 API_KEY=$(openssl rand -hex 24)

SHELL       := /bin/bash
.DEFAULT_GOAL := help

PROJECT     := Qplus.Server.csproj
BINARY      := Qplus.Server
CONFIG      ?= Release
RID         ?= linux-x64
OUT         ?= out

# Install locations
PREFIX      ?= /opt/qplus
DATA_DIR    ?= /var/lib/qplus
DB          ?= $(DATA_DIR)/queries.db
SERVICE     ?= qplus
UNIT        := /etc/systemd/system/$(SERVICE).service
RUN_USER    ?= qplus

# Runtime settings baked into the unit file
HOST        ?= 127.0.0.1
PORT        ?= 5080
URLS        ?= http://$(HOST):$(PORT)
API_KEY     ?=

PUBLISH_FLAGS := -c $(CONFIG) -r $(RID) --self-contained true \
	-p:PublishSingleFile=true \
	-p:IncludeNativeLibrariesForSelfExtract=true \
	-p:EnableCompressionInSingleFile=true

.PHONY: help sdk build publish run clean install uninstall service \
        start stop restart status logs enable disable backup check-root

## ---------------------------------------------------------------- help

help:
	@echo "Qplus query server"
	@echo
	@echo "Prerequisites"
	@echo "  sdk         install the .NET SDK pinned in global.json"
	@echo
	@echo "Build"
	@echo "  build       compile ($(CONFIG))"
	@echo "  publish     single self-contained binary -> $(OUT)/$(BINARY)  [RID=$(RID)]"
	@echo "  run         run locally on $(URLS) with a local database file"
	@echo "  clean       remove build output"
	@echo
	@echo "Deploy   (need root)"
	@echo "  install     publish + install to $(PREFIX), create $(DATA_DIR) and the unit"
	@echo "  service     install, then enable and start it"
	@echo "  uninstall   stop, disable and remove the service and binary (keeps data)"
	@echo
	@echo "Service"
	@echo "  start | stop | restart | status | logs | enable | disable"
	@echo "  backup      snapshot the query database"
	@echo
	@echo "Settings: PORT=$(PORT) HOST=$(HOST) DB=$(DB) RUN_USER=$(RUN_USER)"
	@echo "Set API_KEY=... on install, or the server accepts anyone who can reach it."

## ---------------------------------------------------------------- sdk

# Installs the SDK version pinned in global.json (falls back to the newest .NET 10
# SDK if that preview build has been retired from the CDN).
sdk:
	@../../scripts/install-dotnet.sh $(if $(API_KEY),,)

## ---------------------------------------------------------------- build

build:
	dotnet build $(PROJECT) -c $(CONFIG)

publish:
	@rm -rf $(OUT)
	dotnet publish $(PROJECT) $(PUBLISH_FLAGS) -o $(OUT)
	@rm -f $(OUT)/*.pdb $(OUT)/*.staticwebassets.endpoints.json
	@echo
	@echo "Built $(OUT)/$(BINARY) ($$(du -h $(OUT)/$(BINARY) | cut -f1)) — copy that one file to the server."

# Run straight from source with a database in the working directory.
run:
	QPLUS_DB=$${QPLUS_DB:-./queries.db} \
	QPLUS_URLS=$(URLS) \
	QPLUS_API_KEY=$(API_KEY) \
	dotnet run --project $(PROJECT) --no-launch-profile

clean:
	rm -rf $(OUT) bin obj

## ---------------------------------------------------------------- install

check-root:
	@if [ "$$(id -u)" -ne 0 ]; then echo "This target needs root — try: sudo make $(MAKECMDGOALS)"; exit 1; fi

install: check-root
	@test -f $(OUT)/$(BINARY) || $(MAKE) publish
	@echo "==> service account"
	@id -u $(RUN_USER) >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin $(RUN_USER)
	@echo "==> directories"
	install -d -o $(RUN_USER) -g $(RUN_USER) -m 0750 $(DATA_DIR)
	install -d -m 0755 $(PREFIX)
	@echo "==> binary"
	install -o root -g root -m 0755 $(OUT)/$(BINARY) $(PREFIX)/$(BINARY)
	@echo "==> systemd unit -> $(UNIT)"
	@printf '%s\n' \
	  '[Unit]' \
	  'Description=Qplus query server' \
	  'After=network.target' \
	  '' \
	  '[Service]' \
	  'Type=simple' \
	  'User=$(RUN_USER)' \
	  'ExecStart=$(PREFIX)/$(BINARY)' \
	  'Environment=QPLUS_DB=$(DB)' \
	  'Environment=QPLUS_URLS=$(URLS)' \
	  'Environment=QPLUS_API_KEY=$(API_KEY)' \
	  'Restart=on-failure' \
	  'RestartSec=5' \
	  'NoNewPrivileges=true' \
	  'PrivateTmp=true' \
	  'ProtectSystem=full' \
	  'ProtectHome=true' \
	  'ReadWritePaths=$(DATA_DIR)' \
	  '' \
	  '[Install]' \
	  'WantedBy=multi-user.target' > $(UNIT)
	@chmod 0644 $(UNIT)
	systemctl daemon-reload
	@echo
	@echo "Installed. Start it with:  sudo make service"
	@if [ -z "$(API_KEY)" ]; then \
	  echo; \
	  echo "WARNING: no API_KEY set — anyone who can reach $(URLS) can read and write the"; \
	  echo "         query library. Reinstall with API_KEY=... or edit $(UNIT)."; \
	fi

service: install
	systemctl enable --now $(SERVICE)
	@sleep 1
	@systemctl --no-pager --lines=0 status $(SERVICE) || true
	@echo
	@echo "Health check:  curl -s $(URLS)/api/v1/health"

uninstall: check-root
	-systemctl disable --now $(SERVICE)
	rm -f $(UNIT)
	systemctl daemon-reload
	rm -f $(PREFIX)/$(BINARY)
	@echo "Removed the service and binary. Data kept at $(DATA_DIR) — delete it by hand if you mean to."

## ---------------------------------------------------------------- service

start:
	systemctl start $(SERVICE)

stop:
	systemctl stop $(SERVICE)

restart:
	systemctl restart $(SERVICE)

enable:
	systemctl enable $(SERVICE)

disable:
	systemctl disable $(SERVICE)

status:
	@systemctl --no-pager --lines=0 status $(SERVICE) || true
	@echo
	@curl -fsS $(URLS)/api/v1/health && echo || echo "(health endpoint did not answer)"

logs:
	journalctl -u $(SERVICE) -f --no-pager

backup:
	@mkdir -p $${BACKUP_DIR:-.}
	sqlite3 $(DB) ".backup '$${BACKUP_DIR:-.}/queries-$$(date +%F-%H%M).db'"
	@echo "Wrote $${BACKUP_DIR:-.}/queries-$$(date +%F-%H%M).db"
