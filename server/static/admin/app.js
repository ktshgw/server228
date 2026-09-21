(() => {
    "use strict";

    const API_ROOT = "/api/private/admin-panel";
    const numberFormat = new Intl.NumberFormat("ru-RU");
    const dateFormat = new Intl.DateTimeFormat("ru-RU", {
        dateStyle: "medium",
        timeStyle: "short",
    });

    const state = {
        session: null,
        csrf: null,
        currentView: "dashboard",
        dashboard: null,
        countries: null,
        users: { page: 1, pages: 1, query: "", total: 0 },
        activeUser: null,
        somsaiPools: [],
        somsaiSelected: null,
        somsaiImport: null,
        somsaiBusy: false,
        somsaiSlot: "NM1",
        somsaiCategory: "NM",
        somsaiMapPage: 1,
        somsaiImportPreview: null,
        somsaiWarehouseImportRunning: false,
        negativePPTarget: null,
        negativePPBusy: false,
        scoreImportPreview: null,
        rankingSet: null,
        audit: null,
        auditTab: "account",
        busy: 0,
    };

    const views = {
        "negative-pp": { kicker: "Отрицательные PP", title: "Расстрельный список", capability: "administrator" },
        dashboard: { kicker: "Главная", title: "Обзор сервера", capability: "staff" },
        users: { kicker: "Аккаунты", title: "Пользователи", capability: "administrator" },
        imports: { kicker: "Перенос результатов", title: "Импорт score", capability: "owner" },
        beatmaps: { kicker: "Локальный рейтинг", title: "Карты", capability: "ranker" },
        inbox: { kicker: "Мониторинг карт", title: "Уведомления", capability: "ranker" },
        audit: { kicker: "Безопасность", title: "История действий", capability: "administrator" },
        system: { kicker: "Диагностика", title: "Система", capability: "staff" },
        somsai: { kicker: "Турнирный мультиплеер", title: "SOMSAI", capability: "administrator" },
    };

    const $ = (selector, root = document) => root.querySelector(selector);
    const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

    class ApiError extends Error {
        constructor(status, detail) {
            super(errorMessage(detail));
            this.status = status;
            this.detail = detail;
        }
    }

    function errorMessage(detail) {
        if (typeof detail === "string") return translateError(detail);
        if (detail && typeof detail.message === "string") return translateError(detail.message);
        if (Array.isArray(detail)) {
            return detail.map((item) => item.msg || "Некорректное значение").join("; ");
        }
        return "Сервер не смог выполнить запрос";
    }

    function translateError(message) {
        if (message.startsWith("This official score was already imported as local score")) {
            return message.replace("This official score was already imported as local score", "Этот результат уже импортирован как локальный score");
        }
        if (message.startsWith("This official score is already imported as local score")) {
            return message.replace("This official score is already imported as local score", "Этот результат уже импортирован как локальный score");
        }
        if (message.startsWith("This replay was already imported as local score")) {
            return message.replace("This replay was already imported as local score", "Этот replay уже импортирован как локальный score");
        }
        if (message.startsWith("Official osu! returned HTTP")) {
            return message.replace("Official osu! returned HTTP", "Официальный osu! API ответил кодом");
        }
        const known = {
            "Invalid credentials or staff access": "Неверный логин, пароль или у аккаунта нет доступа к панели",
            "Two-factor code required": "Введите правильный код двухфакторной защиты",
            "Too many login attempts": "Слишком много попыток. Подождите несколько минут",
            "Admin login required": "Войдите в панель администратора",
            "Admin session expired": "Сессия истекла. Войдите ещё раз",
            "Admin session changed browser": "Сессия привязана к другому браузеру. Войдите ещё раз",
            "Staff access required": "У аккаунта больше нет доступа к панели",
            "Invalid CSRF token": "Защитный токен устарел. Обновите страницу",
            "Invalid request origin": "Панель открыта не с адреса этого сервера",
            "User not found": "Пользователь не найден",
            "Editable user not found": "Этот аккаунт нельзя редактировать",
            "No account changes supplied": "Нет изменений для сохранения",
            "Account fields cannot be null": "Поля аккаунта нельзя оставлять пустыми",
            "Unknown ISO 3166 country code": "Неизвестный код страны",
            "Username is invalid": "Имя пользователя не подходит под правила osu!",
            "Username is already taken": "Такое имя пользователя уже занято",
            "The supplied values do not change this account": "Указанные значения уже установлены",
            "Only an owner can manage administrators and owners": "Только владелец может изменять администраторов и владельцев",
            "You cannot deactivate your own account": "Нельзя отключить собственный аккаунт",
            "You cannot remove your own current access": "Нельзя снять с себя текущий доступ",
            "The last active owner cannot be removed": "Нельзя снять права с последнего активного владельца",
            "Ranking event not found": "Уведомление уже удалено или не найдено",
            "Official beatmap metadata is unavailable": "Сейчас не удалось получить карту с osu!. Попробуйте позже",
            "Target user not found": "Локальный пользователь не найден",
            "Scores cannot be imported into an inactive account": "Нельзя импортировать результат в отключённый аккаунт",
            "Scores cannot be imported into a restricted account": "Нельзя импортировать результат в ограниченный аккаунт",
            "This official score has already been imported": "Этот результат уже импортирован",
            "The official score was not found or is not public": "Официальный результат не найден или не является публичным",
            "Could not reach the official osu! API": "Не удалось связаться с API osu!. Попробуйте позже",
            "Could not reach the official osu! beatmap API": "Не удалось получить карту из API osu!. Попробуйте позже",
            "Use an official https://osu.ppy.sh/scores/... link or a numeric score ID": "Укажите официальную ссылку osu! на результат или его числовой ID",
            "The link is not an official osu! score link": "Это не официальная ссылка osu! на результат",
            "Could not find a score ID in the official osu! link": "В ссылке не найден ID результата",
            "The supplied ruleset does not match the score URL": "Выбранный режим не совпадает с режимом в ссылке",
            "Failed plays are not imported into local rankings": "Неудачные прохождения нельзя импортировать в локальный рейтинг",
            "The score belongs to an older beatmap revision; importing it as ranked would be unsafe": "Результат сыгран на старой версии карты; безопасный импорт в рейтинг невозможен",
            "The refreshed local beatmap has an invalid checksum": "У свежей версии карты некорректная контрольная сумма",
            "The beatmap revision check does not belong to this score": "Проверка версии карты не относится к этому результату",
            "The beatmap revision changed during import; check the score again": "Версия карты изменилась во время импорта. Проверьте score ещё раз",
            "Choose a replay file with the .osr extension": "Выберите файл replay с расширением .osr",
            "The uploaded replay is empty": "Выбранный файл replay пуст",
            "The uploaded replay exceeds the 32 MiB safety limit": "Файл replay больше допустимых 32 МиБ",
            "The replay beatmap was not found; enter its difficulty ID and try again": "Карта из replay не нашлась автоматически. Укажите ID сложности карты и повторите проверку",
            "The supplied beatmap ID does not match the replay": "Указанный ID сложности не совпадает с картой из replay",
            "This replay has already been imported": "Этот replay уже был импортирован",
        };
        return known[message] || message;
    }

    async function api(path, options = {}) {
        const method = options.method || "GET";
        if (options.data && Object.prototype.hasOwnProperty.call(options.data, "reason")) {
            options.data.reason = String(options.data.reason || "").trim() || "no reason";
        }
        const headers = new Headers({ Accept: "application/json" });
        if (options.data !== undefined) headers.set("Content-Type", "application/json");
        if (state.csrf && !["GET", "HEAD", "OPTIONS"].includes(method)) {
            headers.set("X-CSRF-Token", state.csrf);
        }

        let response;
        try {
            response = await fetch(`${API_ROOT}${path}`, {
                method,
                headers,
                credentials: "same-origin",
                body: options.form !== undefined
                    ? options.form
                    : options.data === undefined
                        ? undefined
                        : JSON.stringify(options.data),
                signal: options.signal,
            });
        } catch (error) {
            if (error.name === "AbortError") throw error;
            throw new ApiError(0, "Нет связи с сервером. Проверьте, что он запущен");
        }

        let payload = null;
        if (response.status !== 204) {
            const contentType = response.headers.get("content-type") || "";
            payload = contentType.includes("application/json") ? await response.json() : await response.text();
        }
        if (!response.ok) {
            let detail = payload;
            if (payload && Object.hasOwn(payload, "detail")) detail = payload.detail;
            else if (payload && Object.hasOwn(payload, "error")) detail = payload.error;
            if (response.status === 401 && state.session && path !== "/session") {
                showLogin("Сессия закончилась. Войдите ещё раз.");
            }
            throw new ApiError(response.status, detail);
        }
        return payload;
    }

    function make(tag, className, text) {
        const element = document.createElement(tag);
        if (className) element.className = className;
        if (text !== undefined && text !== null) element.textContent = String(text);
        return element;
    }

    function initials(name) {
        const clean = String(name || "?").trim();
        return [...clean].slice(0, 2).join("").toUpperCase() || "?";
    }

    function flagEmoji(code) {
        const upper = String(code || "XX").toUpperCase();
        if (!/^[A-Z]{2}$/.test(upper) || upper === "XX") return "🌐";
        return String.fromCodePoint(...[...upper].map((char) => 127397 + char.charCodeAt(0)));
    }

    function formatNumber(value) {
        return numberFormat.format(Number(value || 0));
    }

    function formatAccuracy(value) {
        let accuracy = Number(value || 0);
        if (accuracy > 0 && accuracy <= 1) accuracy *= 100;
        return `${accuracy.toFixed(2)}%`;
    }

    function formatDate(value) {
        if (!value) return "никогда";
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? "—" : dateFormat.format(date);
    }

    function formatDuration(totalSeconds) {
        let seconds = Math.max(0, Number(totalSeconds || 0));
        const days = Math.floor(seconds / 86400);
        seconds %= 86400;
        const hours = Math.floor(seconds / 3600);
        const minutes = Math.floor((seconds % 3600) / 60);
        if (days) return `${days} дн. ${hours} ч.`;
        if (hours) return `${hours} ч. ${minutes} мин.`;
        return `${minutes} мин.`;
    }

    function roleName(user) {
        if (user.roles?.owner) return "Владелец";
        if (user.roles?.administrator) return "Администратор";
        if (user.roles?.ranker) return "Ранкер";
        if (user.roles?.moderator) return "Модератор";
        return "Сотрудник";
    }

    function hasCapability(capability) {
        const roles = state.session?.user?.roles || {};
        if (capability === "staff") return Boolean(state.session);
        return Boolean(roles[capability]);
    }

    function setBusy(active) {
        state.busy = Math.max(0, state.busy + (active ? 1 : -1));
        $("#page-loading").hidden = state.busy === 0;
    }

    async function busy(task) {
        setBusy(true);
        try {
            return await task();
        } finally {
            setBusy(false);
        }
    }

    function setButtonBusy(button, active, label) {
        if (!button.dataset.label) button.dataset.label = button.textContent;
        button.disabled = active;
        button.textContent = active ? label : button.dataset.label;
    }

    function toast(message, type = "success") {
        const item = make("div", `toast${type === "error" ? " is-error" : ""}`, message);
        $("#toast-region").append(item);
        window.setTimeout(() => item.remove(), 4600);
    }

    function setLoginError(message) {
        const element = $("#login-error");
        element.textContent = message || "";
        element.hidden = !message;
    }

    function showLogin(message = "") {
        state.session = null;
        state.csrf = null;
        state.dashboard = null;
        state.rankingSet = null;
        $("#score-import-form").reset();
        $("#score-import-ruleset").disabled = false;
        $("#score-import-replay").disabled = false;
        invalidateScoreImportPreview();
        $("#admin-app").hidden = true;
        $("#login-screen").hidden = false;
        $("#login-password").value = "";
        $("#login-totp").value = "";
        $("#totp-field").hidden = true;
        setLoginError(message);
        $("#login-username").focus();
    }

    function showApp(session) {
        state.session = session;
        state.csrf = session.csrf_token;
        $("#login-screen").hidden = true;
        $("#admin-app").hidden = false;
        $("#profile-name").textContent = session.user.username;
        $("#profile-role").textContent = roleName(session.user);
        $("#profile-avatar").textContent = initials(session.user.username);
        $("#welcome-name").textContent = session.user.username;

        $$('[data-capability]').forEach((element) => {
            element.hidden = !hasCapability(element.dataset.capability);
        });

        const requested = location.hash.slice(1);
        const initial = views[requested] && hasCapability(views[requested].capability) ? requested : "dashboard";
        navigate(initial);
        loadCountries().catch(() => undefined);
    }

    async function handleLogin(event) {
        event.preventDefault();
        const button = $("#login-submit");
        setLoginError("");
        setButtonBusy(button, true, "Проверяем…");
        try {
            const result = await api("/session", {
                method: "POST",
                data: {
                    username: $("#login-username").value.trim(),
                    password: $("#login-password").value,
                    totp_code: $("#login-totp").value.trim() || null,
                },
            });
            $("#login-form").reset();
            showApp(result);
        } catch (error) {
            if (error instanceof ApiError && error.detail?.totp_required) {
                $("#totp-field").hidden = false;
                $("#login-totp").focus();
            }
            setLoginError(error.message || "Не удалось войти");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function logout() {
        const button = $("#logout-button");
        button.disabled = true;
        try {
            await api("/session", { method: "DELETE" });
        } catch (error) {
            if (error.status !== 401) toast(error.message, "error");
        } finally {
            button.disabled = false;
            history.replaceState(null, "", `${location.pathname}${location.search}`);
            showLogin("Вы вышли из панели.");
        }
    }

    function navigate(view) {
        const config = views[view];
        if (!config || !hasCapability(config.capability)) return;
        state.currentView = view;
        $$("[data-page]").forEach((page) => {
            const active = page.dataset.page === view;
            page.hidden = !active;
            page.classList.toggle("is-active", active);
        });
        $$("[data-view]").forEach((item) => item.classList.toggle("is-active", item.dataset.view === view));
        $("#page-kicker").textContent = config.kicker;
        $("#page-title").textContent = config.title;
        history.replaceState(null, "", `#${view}`);
        refreshCurrent();
    }

    async function refreshCurrent() {
        const loaders = {
            "negative-pp": loadNegativePP,
            dashboard: loadDashboard,
            users: loadUsers,
            imports: loadScoreImports,
            beatmaps: loadPolicies,
            inbox: loadEvents,
            audit: loadAudit,
            system: loadSystem,
            somsai: loadSomsai,
        };
        try {
            await busy(loaders[state.currentView]);
        } catch (error) {
            if (error.name !== "AbortError") toast(error.message || "Не удалось загрузить данные", "error");
        }
    }

    async function negativePPAction(action) {
        if (state.negativePPBusy) return;
        state.negativePPBusy = true;
        const buttons = $$("#view-negative-pp button");
        buttons.forEach((button) => button.disabled = true);
        $("#negative-pp-status").textContent = "Обработка… При добавлении маппера проверяется авторство старых карт.";
        try { await action(); }
        catch (error) { toast(error.message, "error"); $("#negative-pp-status").textContent = error.message; }
        finally {
            state.negativePPBusy = false;
            buttons.forEach((button) => button.disabled = false);
            $("#negative-pp-add").disabled = !state.negativePPTarget;
        }
    }

    async function loadNegativePP() {
        const data = await api("/negative-pp");
        const list = $("#negative-pp-list");
        list.replaceChildren();
        if (!data.items.length) list.append(make("p", "muted", "Список пуст. PP начисляются как обычно."));
        const kinds = { beatmap: "Сложность", beatmapset: "Мапсет", mapper: "Маппер" };
        for (const rule of data.items) {
            const card = make("article", "panel stack");
            const link = make("a", "", `${kinds[rule.kind]} #${rule.target_id}: ${rule.label}`);
            const paths = { beatmap: "beatmaps", beatmapset: "beatmapsets", mapper: "users" };
            link.href = `https://osu.ppy.sh/${paths[rule.kind]}/${Number(rule.target_id)}`;
            link.target = "_blank"; link.rel = "noopener noreferrer";
            const form = make("form", "button-row");
            const reasonField = make("label", "field");
            const reason = make("input");
            reason.placeholder = "Причина (необязательно)"; reason.maxLength = 500;
            reason.setAttribute("aria-label", `Причина удаления ${rule.label}`);
            const button = make("button", "button button-secondary", "Удалить и пересчитать PP");
            button.type = "submit";
            reasonField.append(make("span", "", "Причина удаления"), reason);
            form.append(reasonField, button);
            form.addEventListener("submit", (event) => {
                event.preventDefault();
                negativePPAction(async () => {
                    const result = await api(`/negative-pp/${rule.id}`, { method: "DELETE", data: { reason: reason.value.trim() || "no reason" } });
                    $("#negative-pp-status").textContent = `Правило удалено. Пересчитано профилей по режимам: ${result.user_modes}.`;
                    await loadNegativePP();
                });
            });
            card.append(link, make("p", "muted", rule.reason), form);
            list.append(card);
        }
    }

    async function loadSomsaiLegacy(selectedId = Number($("#somsai-select").value)) {
        state.somsaiPools = (await api("/somsai/pools")).pools;
        const select = $("#somsai-select");
        select.replaceChildren(new Option("Новый пул", ""), ...state.somsaiPools.map(pool =>
            new Option(`${pool.active ? "●" : "○"} ${pool.name} · BO${pool.best_of} · ${pool.slots.length} карт`, String(pool.id))));
        select.value = state.somsaiPools.some(pool => pool.id === selectedId) ? String(selectedId) : "";
        openSomsai();
    }

    function openSomsai() {
        const pool = state.somsaiPools.find(item => item.id === Number($("#somsai-select").value));
        state.somsaiSelected = pool || null;
        $("#somsai-name").value = pool?.name || "";
        $("#somsai-mode").value = `${pool?.ruleset_id || 0}:${pool?.variant_id || 0}`;
        [["rating-min", "rating_min", 0], ["rating-max", "rating_max", 5000], ["bo", "best_of", 7],
            ["bans", "bans_per_team", 1], ["rank-min", "source_rank_min", ""], ["rank-max", "source_rank_max", ""]]
            .forEach(([id, key, fallback]) => { $(`#somsai-${id}`).value = pool?.[key] ?? fallback; });
        $("#somsai-slots").value = (pool?.slots || []).map(slot => `${slot.id} ${slot.beatmap_id}`).join("\n");
        $("#somsai-active").value = String(Boolean(pool?.active));
        $("#somsai-reason").value = "";
        $("#somsai-delete").disabled = !pool;
        $("#somsai-source").textContent = pool ? `Версия ${pool.revision} · Оценка MMR SOMSAI: ${pool.estimated_mmr ?? "—"} (по ★) · ${pool.average_stars == null ? "★ —" : `${pool.average_stars}★ без модов`} · ${pool.source_url || "Ручной набор"}${pool.source_round ? ` · ${pool.source_round}` : ""}` : "";
        $("#somsai-result").replaceChildren();
        invalidateSomsaiImport();
    }

    function somsaiSpec() {
        const [ruleset_id, variant_id] = $("#somsai-mode").value.split(":").map(Number);
        const old = state.somsaiSelected?.slots || [];
        const slots = $("#somsai-slots").value.split(/\r?\n/).map(line => line.trim()).filter(Boolean).map(line => {
            const match = /^((?:NM|HD|HR|DT|FM)[1-9][0-9]?|TB)\s+([1-9][0-9]*)$/.exec(line.toUpperCase());
            if (!match) throw new Error(`Некорректная строка: ${line}`);
            const preserved = old.find(slot => slot.id === match[1] && slot.beatmap_id === Number(match[2]));
            return { id: match[1], beatmap_id: Number(match[2]), checksum: preserved?.checksum || null,
                source_url: preserved?.source_url || null };
        });
        return { name: $("#somsai-name").value.trim(), ruleset_id, variant_id, slots,
            active: $("#somsai-active").value === "true", rating_min: Number($("#somsai-rating-min").value),
            rating_max: Number($("#somsai-rating-max").value), best_of: Number($("#somsai-bo").value),
            bans_per_team: Number($("#somsai-bans").value),
            source_rank_min: $("#somsai-rank-min").value ? Number($("#somsai-rank-min").value) : null,
            source_rank_max: $("#somsai-rank-max").value ? Number($("#somsai-rank-max").value) : null };
    }

    async function somsaiAction(action) {
        if (state.somsaiBusy) return;
        state.somsaiBusy = true;
        const controls = $$("input, select, textarea, button", $("#view-somsai"));
        const disabled = controls.map(control => control.disabled);
        controls.forEach(control => { control.disabled = true; });
        try { await action(); } catch (error) {
            const errors = error.detail?.errors;
            toast(`${error.message}${errors?.length ? `: ${errors.join("; ")}` : ""}`, "error");
        } finally {
            state.somsaiBusy = false;
            controls.forEach((control, index) => { control.disabled = disabled[index]; });
            $("#somsai-delete").disabled = !state.somsaiSelected;
            updateSomsaiImportButtons();
        }
    }

    function renderSomsaiCheck(result, target = "#somsai-result") {
        const root = $(target);
        root.replaceChildren();
        [...(result.errors || []), ...(result.warnings || [])].forEach(error => root.append(make("p", "error-message", error)));
        if (result.playable) root.append(make("p", "", "Пул готов к активации."));
        (result.slots || []).forEach(slot => root.append(make("p", "muted", `${slot.id} · #${slot.beatmap_id}${slot.name ? ` · ${slot.name}` : ""}`)));
    }

    function invalidateSomsaiImport() {
        state.somsaiImport = null;
        updateSomsaiImportButtons();
    }

    function updateSomsaiImportButtons() {
        $("#somsai-import-create").disabled = !state.somsaiImport?.slots.length;
        $("#somsai-import-append").disabled = !state.somsaiImport?.slots.length || !state.somsaiSelected;
        $("#somsai-warehouse-import").disabled = !state.somsaiImportPreview || state.somsaiWarehouseImportRunning;
    }

    function renderWarehouseImport(result) {
        state.somsaiWarehouseImportRunning = Boolean(result.running);
        updateSomsaiImportButtons();
        const text = result.error
            ? `Ошибка импорта: ${result.error}`
            : result.running
                ? `Импортируется: ${result.done} из ${result.total || "…"}; ошибок: ${result.failed}`
                : `Импортировано: ${result.imported}; ошибок: ${result.failed}`;
        $("#somsai-warehouse-import-result").replaceChildren(
            make("p", result.error || result.failed ? "error-message" : "muted", text)
        );
    }

    async function pollWarehouseImport() {
        try {
            const result = await api("/somsai/warehouse/import");
            renderWarehouseImport(result);
            if (result.running) {
                window.setTimeout(pollWarehouseImport, 1000);
            } else {
                await loadSomsaiMaps();
            }
        } catch (error) {
            state.somsaiWarehouseImportRunning = false;
            updateSomsaiImportButtons();
            toast(error.message, "error");
        }
    }

    function somsaiImportRequest() {
        return { url: $("#somsai-import-url").value.trim(), round: $("#somsai-import-round").value || null,
            category: $("#somsai-import-category").value };
    }

    async function readSomsaiSource() {
        await somsaiAction(async () => {
            const result = await api("/somsai/import/preview", { method: "POST", data: somsaiImportRequest() });
            const current = $("#somsai-import-round").value;
            $("#somsai-import-round").replaceChildren(new Option("Выберите раунд", ""), ...result.rounds.map(round => new Option(round, round)));
            $("#somsai-import-round").value = result.rounds.includes(current) ? current : "";
            state.somsaiImport = result;
            renderSomsaiCheck(result, "#somsai-import-result");
            if (!result.slots.length) $("#somsai-import-result").append(make("p", "muted", "Выберите раунд и нажмите «Прочитать источник» ещё раз."));
            if (!$("#somsai-name").value) $("#somsai-name").value = result.name;
        });
    }

    async function importSomsai(append) {
        if (!state.somsaiImport?.slots.length || !$("#somsai-form").reportValidity()) return;
        await somsaiAction(async () => {
            const selected = append ? state.somsaiSelected : null;
            const data = { ...somsaiImportRequest(), append, pool: { ...somsaiSpec(), slots: [], active: false,
                expected_revision: selected?.revision || null, reason: $("#somsai-reason").value.trim() } };
            const saved = await api(selected ? `/somsai/pools/${selected.id}/import` : "/somsai/import", { method: "POST", data });
            await loadSomsaiLegacy(saved.id);
            toast("Источник импортирован. Проверьте черновик и включите пул.");
        });
    }

    const somsaiSlots = {
        NM: ["NM1", "NM2", "NM3", "NM4", "NM5", "NM6"], HD: ["HD1", "HD2", "HD3"],
        HR: ["HR1", "HR2", "HR3"], DT: ["DT1", "DT2", "DT3", "DT4"],
        FM: ["FM1", "FM2", "FM3"], TB: ["TB"],
    };

    function showSomsaiPage(page) {
        $$("[data-somsai-page]").forEach(button => {
            button.classList.toggle("is-active", button.dataset.somsaiPage === page);
            button.classList.toggle("button-primary", button.dataset.somsaiPage === page);
            button.classList.toggle("button-secondary", button.dataset.somsaiPage !== page);
        });
        $$(".somsai-subpage").forEach(panel => { panel.hidden = panel.id !== `somsai-${page}-page`; });
    }

    function renderSomsaiSlots(category) {
        state.somsaiCategory = category;
        const slots = somsaiSlots[category];
        if (!slots.includes(state.somsaiSlot)) state.somsaiSlot = slots[0];
        $$("[data-somsai-category]").forEach(button => button.classList.toggle("is-active", button.dataset.somsaiCategory === category));
        const root = $("#somsai-slot-tabs");
        root.replaceChildren(...slots.map(slot => {
            const button = make("button", slot === state.somsaiSlot ? "is-active" : "", slot);
            button.type = "button";
            button.addEventListener("click", () => { state.somsaiSlot = slot; state.somsaiMapPage = 1; renderSomsaiSlots(category); loadSomsaiMaps(); });
            return button;
        }));
    }

    function renderSomsaiMap(row) {
        const card = make("article", "somsai-map-card");
        if (row.cover_url) card.style.backgroundImage = `linear-gradient(90deg, rgba(12,16,27,.96), rgba(12,16,27,.72)), url(${JSON.stringify(row.cover_url).slice(1, -1)})`;
        const copy = make("div", "somsai-map-copy");
        const title = make("a", "", row.name);
        title.href = row.beatmap_url; title.target = "_blank"; title.rel = "noopener noreferrer";
        copy.append(title, make("small", "", `#${row.beatmap_id} · ${row.slot} · ${row.eligibility_label}`));
        const stats = row.stats || {};
        const facts = make("div", "somsai-map-stats");
        [["★", stats.stars], ["TIME", stats.length == null ? "—" : `${Math.floor(stats.length / 60)}:${String(Math.round(stats.length % 60)).padStart(2, "0")}`],
            ["CS", stats.cs], ["AR", stats.ar], ["OD", stats.od], ["HP", stats.hp]].forEach(([key, value]) => facts.append(make("span", "", `${key} ${value ?? "—"}`)));
        const remove = make("button", "button button-danger", "Удалить");
        remove.type = "button";
        remove.addEventListener("click", async () => {
            if (!confirm(`Удалить ${row.slot} #${row.beatmap_id} из хранилища?`)) return;
            await api(`/somsai/maps/${row.id}`, { method: "DELETE", data: { reason: "no reason" } });
            await loadSomsaiMaps();
        });
        card.append(copy, facts, remove);
        return card;
    }

    async function loadSomsaiMaps() {
        const result = await api(`/somsai/maps?slot=${encodeURIComponent(state.somsaiSlot)}&page=${state.somsaiMapPage}`);
        $("#somsai-storage-title").textContent = state.somsaiSlot;
        $("#somsai-storage-count").textContent = `${result.total} карт`;
        $("#somsai-maps-page").textContent = `${result.page} / ${result.pages}`;
        $("#somsai-maps-prev").disabled = result.page <= 1;
        $("#somsai-maps-next").disabled = result.page >= result.pages;
        const root = $("#somsai-map-grid");
        root.replaceChildren(...result.maps.map(renderSomsaiMap));
        if (!result.maps.length) root.append(make("p", "muted", "В этом слоте пока нет карт."));
    }

    async function loadSomsai() {
        const all = Object.values(somsaiSlots).flat();
        $("#somsai-map-slot").replaceChildren(...all.map(slot => new Option(slot, slot)));
        renderSomsaiSlots(state.somsaiCategory);
        await loadSomsaiMaps();
        const refresh = await api("/somsai/warehouse/refresh");
        renderSomsaiRefresh(refresh);
    }

    function renderSomsaiRefresh(status) {
        $("#somsai-refresh-status").textContent = status.running
            ? `Обновление: ${status.done} / ${status.total}, ошибок: ${status.failed}`
            : status.total ? `Последнее обновление: ${status.done} / ${status.total}, ошибок: ${status.failed}` : "";
    }

    function metricCard(label, value, note, icon) {
        const card = make("article", "metric-card");
        const head = make("div", "metric-head");
        head.append(make("span", "", label), make("span", "metric-icon", icon));
        card.append(head, make("strong", "metric-value", value), make("small", "metric-note", note));
        return card;
    }

    function healthRow(label, ok) {
        const row = make("div", "health-row");
        const left = make("span", "health-label");
        left.append(make("span", `health-indicator${ok ? " is-ok" : ""}`), make("span", "", label));
        row.append(left, make("span", "health-state", ok ? "Работает" : "Ошибка"));
        return row;
    }

    async function loadDashboard() {
        const tasks = [api("/dashboard")];
        if (hasCapability("ranker")) tasks.push(api("/ranking/events?limit=3"));
        const [data, eventsData] = await Promise.all(tasks);
        state.dashboard = data;

        const metrics = $("#dashboard-metrics");
        metrics.replaceChildren(
            metricCard("Пользователи", formatNumber(data.users.total), `${formatNumber(data.users.online)} сейчас онлайн`, "◉"),
            metricCard("Результаты", formatNumber(data.scores.total), `${formatNumber(data.scores.last_24h)} за последние сутки`, "⌁"),
            metricCard("Локальный ранг", formatNumber(data.ranking.active_set_policies + data.ranking.active_difficulty_policies), `${formatNumber(data.ranking.active_difficulty_policies)} отдельных сложностей`, "◇"),
            metricCard("Уведомления", formatNumber(data.ranking.pending_events), data.ranking.pending_events ? "нужно проверить" : "всё спокойно", "◌"),
        );

        $("#dashboard-health").replaceChildren(
            healthRow("API сервера", data.system.api),
            healthRow("База данных", data.system.database),
            healthRow("Redis и сессии", data.system.redis),
        );
        const allOk = data.system.api && data.system.database && data.system.redis;
        const serverStatus = $("#sidebar-server-status");
        serverStatus.classList.toggle("is-ok", allOk);
        serverStatus.classList.toggle("is-bad", !allOk);
        serverStatus.lastElementChild.textContent = allOk ? "Все системы работают" : "Есть проблема с сервисом";

        const pending = Number(data.ranking.pending_events || 0);
        const badge = $("#nav-inbox-badge");
        badge.textContent = pending > 99 ? "99+" : String(pending);
        badge.hidden = pending === 0;
        renderDashboardInbox(eventsData?.items || [], pending);
    }

    function renderDashboardInbox(events, pending) {
        const root = $("#dashboard-inbox");
        root.replaceChildren();
        if (!events.length) {
            root.className = "empty-state compact";
            root.append(make("span", "empty-icon", "✓"), make("strong", "", "Пока всё спокойно"), make("p", "", "Новых событий нет."));
            return;
        }
        root.className = "health-list";
        events.forEach((event) => {
            const row = make("div", "health-row");
            const left = make("span", "health-label");
            left.append(make("span", "health-indicator"), make("span", "", `Сет #${event.beatmapset_id}${event.beatmap_id ? ` · ${event.beatmap_id}` : ""}`));
            row.append(left, make("span", "health-state", formatDate(event.created_at)));
            root.append(row);
        });
        if (pending > events.length) root.append(make("p", "form-hint", `И ещё ${pending - events.length} событий`));
    }

    async function loadCountries() {
        if (state.countries) return state.countries;
        state.countries = await api("/countries");
        return state.countries;
    }

    function appendRoleTags(root, user) {
        const tags = [];
        if (user.is_owner) tags.push(["Владелец", "is-owner"]);
        else if (user.is_admin) tags.push(["Админ", "is-admin"]);
        if (user.is_bng || user.is_qat) tags.push([user.is_bng ? "BNG" : "QAT", "is-ranker"]);
        if (user.is_gmt) tags.push(["GMT", "is-mod"]);
        if (user.is_supporter) tags.push(["Supporter", ""]);
        if (!tags.length) tags.push(["Игрок", ""]);
        tags.forEach(([label, style]) => root.append(make("span", `tag ${style}`.trim(), label)));
    }

    function accountStatus(user) {
        if (!user.is_active) return ["Отключён", "is-bad"];
        if (user.is_restricted) return ["Ограничен", "is-warn"];
        if (user.is_online) return ["Онлайн", "is-ok"];
        return ["Активен", ""];
    }

    async function loadUsers() {
        const params = new URLSearchParams({
            query: state.users.query,
            page: String(state.users.page),
            page_size: "25",
        });
        const data = await api(`/users?${params}`);
        state.users.page = data.page;
        state.users.pages = data.pages;
        state.users.total = data.total;
        $("#user-count").textContent = `${formatNumber(data.total)} польз.`;
        $("#users-page").textContent = `Страница ${data.page} из ${data.pages}`;
        $("#users-prev").disabled = data.page <= 1;
        $("#users-next").disabled = data.page >= data.pages;

        const body = $("#users-table-body");
        body.replaceChildren();
        $("#users-empty").hidden = data.items.length > 0;
        data.items.forEach((user) => {
            const row = make("tr");
            const identityCell = make("td");
            const identity = make("div", "user-cell");
            identity.append(make("span", "avatar avatar-small", initials(user.username)));
            const names = make("span");
            names.append(make("strong", "", user.username), make("small", "", `ID #${user.server_id || user.id}`));
            identity.append(names);
            identityCell.append(identity);

            const countryCell = make("td", "", `${flagEmoji(user.country_code)} ${user.country_code || "XX"}`);
            const rolesCell = make("td");
            const roles = make("div", "roles");
            appendRoleTags(roles, user);
            rolesCell.append(roles);
            const statusCell = make("td");
            const [statusLabel, statusClass] = accountStatus(user);
            statusCell.append(make("span", `status-badge ${statusClass}`.trim(), statusLabel));
            const lastVisitCell = make("td", "muted", formatDate(user.last_visit));
            const actionCell = make("td");
            const action = make("button", "row-action", "Открыть");
            action.type = "button";
            action.addEventListener("click", () => openUser(user.id));
            actionCell.append(action);
            row.append(identityCell, countryCell, rolesCell, statusCell, lastVisitCell, actionCell);
            body.append(row);
        });
    }

    function fillCountrySelect(selected) {
        const select = $("#detail-country");
        select.replaceChildren();
        (state.countries || []).forEach((country) => {
            const option = make("option", "", `${flagEmoji(country.code)} ${country.name} (${country.code})`);
            option.value = country.code;
            option.selected = country.code === selected;
            select.append(option);
        });
        if (![...select.options].some((option) => option.value === selected)) {
            const fallback = make("option", "", `${flagEmoji(selected)} ${selected}`);
            fallback.value = selected;
            fallback.selected = true;
            select.prepend(fallback);
        }
    }

    function renderUserDetails(user, scores = { items: [], total: 0 }) {
        state.activeUser = user;
        $("#detail-avatar").textContent = initials(user.username);
        $("#detail-id").textContent = `ID #${user.server_id || user.id} · создан ${formatDate(user.join_date)}`;
        $("#detail-name").textContent = user.username;
        $("#detail-username").value = user.username;
        $("#detail-email").textContent = user.email
            || (hasCapability("owner") ? "Почта не указана" : "Почта видна только владельцу");
        const [statusLabel, statusClass] = accountStatus(user);
        const status = $("#detail-status");
        status.className = `status-badge ${statusClass}`.trim();
        status.textContent = statusLabel;
        fillCountrySelect(user.country_code || "XX");
        $("#detail-active").value = String(Boolean(user.is_active));
        ["supporter", "gmt", "qat", "bng", "admin", "owner"].forEach((name) => {
            $(`#detail-${name}`).checked = Boolean(user[`is_${name}`]);
        });
        $("#detail-reason").value = "";

        const owner = hasCapability("owner");
        const protectedTarget = (user.is_owner || user.is_admin) && !owner;
        const editableControls = [
            "#detail-username", "#detail-country", "#detail-active", "#detail-supporter", "#detail-gmt", "#detail-qat",
            "#detail-bng", "#detail-admin", "#detail-owner", "#detail-reason", "#user-save",
        ];
        editableControls.forEach((selector) => { $(selector).disabled = protectedTarget; });
        $("#detail-admin").disabled = !owner || protectedTarget;
        $("#detail-owner").disabled = !owner || protectedTarget;
        if (user.id === state.session.user.id) $("#detail-active").disabled = true;
        $("#password-section").hidden = !owner;
        $("#password-form").hidden = true;
        $("#profile-clear-section").hidden = !owner;
        $("#profile-clear-form").hidden = true;

        renderUserStatistics(user.statistics || []);
        renderUserSomsaiMmr();
        renderUserScores(scores);
        renderUserLogins(user.recent_logins || []);
        selectUserTab("account");
    }

    function renderUserStatistics(statistics) {
        const root = $("#detail-statistics");
        root.replaceChildren();
        if (!statistics.length) {
            root.append(make("div", "empty-state", "Игровой статистики пока нет."));
            return;
        }
        statistics.forEach((stats) => {
            const card = make("article", "stat-card");
            card.append(make("h4", "", String(stats.mode || "osu")));
            const facts = make("div", "stat-facts");
            [
                ["PP", formatNumber(stats.pp)],
                ["Точность", `${Number(stats.accuracy || 0).toFixed(2)}%`],
                ["Игр", formatNumber(stats.play_count)],
                ["Время", formatDuration(stats.play_time)],
                ["Ranked score", formatNumber(stats.ranked_score)],
                ["Total score", formatNumber(stats.total_score)],
            ].forEach(([label, value]) => {
                const fact = make("div", "", label);
                fact.append(make("strong", "", value));
                facts.append(fact);
            });
            card.append(facts);
            root.append(card);
        });
    }

    function somsaiModeLabel(item) {
        const modes = ["osu!", "osu!taiko", "osu!catch", "osu!mania"];
        return `${modes[item.ruleset_id]}${item.ruleset_id === 3 ? ` ${item.variant_id}K` : ""} · ${item.format}`;
    }

    function renderUserSomsaiMmr(selectedPoolId = null) {
        const items = state.activeUser?.somsai_mmr || [];
        const select = $("#somsai-mmr-pool");
        select.replaceChildren();
        items.forEach((item) => {
            const option = make("option", "", somsaiModeLabel(item));
            option.value = String(item.key);
            select.append(option);
        });
        if (items.some((item) => item.key === selectedPoolId)) select.value = String(selectedPoolId);
        $("#somsai-mmr-empty").hidden = items.length > 0;
        $("#somsai-mmr-form").hidden = !items.length;
        updateSelectedSomsaiMmr();
    }

    function updateSelectedSomsaiMmr() {
        const user = state.activeUser;
        const item = user?.somsai_mmr?.find((entry) => entry.key === $("#somsai-mmr-pool").value);
        const protectedTarget = Boolean(user && (user.is_owner || user.is_admin) && !hasCapability("owner"));
        $("#somsai-mmr-permission").hidden = !protectedTarget;
        $("#somsai-mmr-current").textContent = item?.mmr == null
            ? "У игрока ещё нет рейтинга в этом режиме. Можно задать начальное MMR SOMSAI."
            : `Текущее MMR SOMSAI: ${formatNumber(item.mmr)} · Матчей: ${formatNumber(item.games)}`;
        $("#somsai-mmr-value").value = item?.mmr == null ? "" : String(Math.round(item.mmr));
        $("#somsai-mmr-reason").value = "";
        ["#somsai-mmr-value", "#somsai-mmr-reason", "#somsai-mmr-save"].forEach((selector) => {
            $(selector).disabled = protectedTarget || !item;
        });
    }

    async function refreshSomsaiMmr() {
        const user = state.activeUser;
        if (!user) return;
        const poolId = $("#somsai-mmr-pool").value;
        const button = $("#somsai-mmr-refresh");
        setButtonBusy(button, true, "Обновляем…");
        try {
            const updated = await api(`/users/${user.id}`);
            if (state.activeUser?.id !== user.id) return;
            state.activeUser = { ...state.activeUser, ...updated };
            renderUserSomsaiMmr(poolId);
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function saveSomsaiMmr(event) {
        event.preventDefault();
        const user = state.activeUser;
        const pool = user?.somsai_mmr?.find((entry) => entry.key === $("#somsai-mmr-pool").value);
        if (!user || !pool) return;
        const mmr = Number($("#somsai-mmr-value").value);
        const reason = $("#somsai-mmr-reason").value.trim();
        if (!Number.isInteger(mmr) || mmr < 0 || mmr > 5000) {
            toast("Введите целое MMR SOMSAI от 0 до 5000", "error");
            return;
        }
        const button = $("#somsai-mmr-save");
        setButtonBusy(button, true, "Сохраняем…");
        $("#somsai-mmr-pool").disabled = true;
        $("#somsai-mmr-refresh").disabled = true;
        $("#somsai-mmr-value").disabled = true;
        $("#somsai-mmr-reason").disabled = true;
        try {
            const updated = await api(`/users/${user.id}/somsai-mmr/${pool.ruleset_id}/${pool.variant_id}/${pool.format}`, {
                method: "PATCH", data: { mmr, reason: reason || "no reason", expected_version: pool.version },
            });
            if (state.activeUser?.id === user.id) {
                state.activeUser.somsai_mmr = state.activeUser.somsai_mmr.map((item) => item.key === updated.key ? updated : item);
                renderUserSomsaiMmr(updated.key);
            }
            toast(`MMR SOMSAI игрока ${user.username} изменено на ${formatNumber(updated.mmr)}`);
            state.audit = null;
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
            $("#somsai-mmr-pool").disabled = false;
            $("#somsai-mmr-refresh").disabled = false;
            const current = state.activeUser;
            const disabled = !current?.somsai_mmr?.length || ((current.is_owner || current.is_admin) && !hasCapability("owner"));
            ["#somsai-mmr-value", "#somsai-mmr-reason", "#somsai-mmr-save"].forEach((selector) => { $(selector).disabled = disabled; });
        }
    }

    function renderUserScores(payload) {
        const items = Array.isArray(payload?.items) ? payload.items : [];
        $("#detail-score-count").textContent = formatNumber(payload?.total || items.length);
        const root = $("#detail-scores");
        root.replaceChildren();
        if (!items.length) {
            root.append(make("div", "empty-state", "У пользователя пока нет скоров."));
            return;
        }
        items.forEach((score) => {
            const card = make("article", "admin-score-record");
            const grade = make("span", "admin-score-grade", score.rank || "—");
            const copy = make("div", "admin-score-copy");
            const set = score.beatmapset || {};
            const map = score.beatmap || {};
            copy.append(
                make("strong", "", `${set.artist || "Неизвестный артист"} — ${set.title || "Без названия"}`),
                make("span", "", `${map.version || score.ruleset || "osu"} · ${scoreMods(score.mods)} · ${formatDate(score.ended_at)}${score.imported ? " · импорт" : ""}`),
            );
            const numbers = make("div", "admin-score-numbers");
            numbers.append(
                make("strong", "", `${formatNumber(score.pp)} pp`),
                make("span", "", `${formatAccuracy(score.accuracy)} · ${formatNumber(score.total_score)}`),
            );
            const remove = make("button", "button button-danger-quiet admin-score-delete", "Удалить");
            remove.type = "button";
            remove.addEventListener("click", () => deleteUserScore(score, remove));
            card.append(grade, copy, numbers, remove);
            root.append(card);
        });
    }

    async function deleteUserScore(score, button) {
        const user = state.activeUser;
        if (!user) return;
        const set = score.beatmapset || {};
        if (!window.confirm(`Удалить score #${score.id} на ${set.artist || "?"} — ${set.title || "?"}? Статистика будет пересчитана.`)) return;
        const reason = window.prompt("Причина удаления (необязательно):", "");
        if (reason === null) return;
        setButtonBusy(button, true, "Удаляем…");
        try {
            await api(`/users/${user.id}/scores/${score.id}`, { method: "DELETE", data: { reason: reason.trim() || "no reason" } });
            toast(`Score #${score.id} удалён, статистика пересчитана`);
            await openUser(user.id);
            await loadUsers();
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    function renderUserLogins(logins) {
        const root = $("#detail-logins");
        root.replaceChildren();
        if (!logins.length) {
            root.append(make("div", "empty-state", "Истории входов пока нет."));
            return;
        }
        logins.forEach((login) => {
            const row = make("div", "login-record");
            row.append(
                make("strong", "", formatDate(login.time)),
                make("span", "", login.success ? "Успешно" : "Ошибка"),
                make("span", "", `${flagEmoji(login.country_code)} ${login.country_code || "XX"}`),
                make(
                    "span",
                    "",
                    login.ip_address || (hasCapability("owner") ? "IP неизвестен" : "IP виден только владельцу"),
                ),
            );
            root.append(row);
        });
    }

    async function openUser(userId) {
        const dialog = $("#user-dialog");
        $("#user-dialog-loading").hidden = false;
        $("#user-dialog-content").hidden = true;
        if (!dialog.open) dialog.showModal();
        try {
            const [, user, scores] = await Promise.all([
                loadCountries(),
                api(`/users/${userId}`),
                api(`/users/${userId}/scores?limit=100`),
            ]);
            renderUserDetails(user, scores);
            $("#user-dialog-loading").hidden = true;
            $("#user-dialog-content").hidden = false;
        } catch (error) {
            dialog.close();
            toast(error.message, "error");
        }
    }

    function selectUserTab(tab) {
        $$('[data-user-tab]').forEach((button) => button.classList.toggle("is-active", button.dataset.userTab === tab));
        $$(".user-tab").forEach((section) => {
            const active = section.id === `user-tab-${tab}`;
            section.hidden = !active;
            section.classList.toggle("is-active", active);
        });
    }

    async function saveUser(event) {
        event.preventDefault();
        const user = state.activeUser;
        if (!user) return;
        const values = {
            username: $("#detail-username").value.trim(),
            country_code: $("#detail-country").value,
            is_active: $("#detail-active").value === "true",
            is_supporter: $("#detail-supporter").checked,
            is_gmt: $("#detail-gmt").checked,
            is_qat: $("#detail-qat").checked,
            is_bng: $("#detail-bng").checked,
            is_admin: $("#detail-admin").checked,
            is_owner: $("#detail-owner").checked,
        };
        const payload = { reason: $("#detail-reason").value.trim() };
        Object.entries(values).forEach(([key, value]) => {
            if (value !== user[key]) payload[key] = value;
        });
        if (Object.keys(payload).length === 1) {
            toast("Вы ничего не изменили", "error");
            return;
        }
        const button = $("#user-save");
        setButtonBusy(button, true, "Сохраняем…");
        try {
            const updated = await api(`/users/${user.id}`, { method: "PATCH", data: payload });
            if (user.id === state.session.user.id) {
                state.session.user = { ...state.session.user, ...updated };
                $("#profile-name").textContent = updated.username;
                $("#profile-avatar").textContent = initials(updated.username);
                $("#welcome-name").textContent = updated.username;
            }
            toast("Изменения сохранены");
            await openUser(user.id);
            await loadUsers();
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function resetPassword(event) {
        event.preventDefault();
        const user = state.activeUser;
        if (!user) return;
        const password = $("#new-password").value;
        if (password !== $("#confirm-password").value) {
            toast("Пароли не совпадают", "error");
            return;
        }
        const button = event.submitter;
        setButtonBusy(button, true, "Меняем…");
        try {
            await api(`/users/${user.id}/password`, {
                method: "POST",
                data: { new_password: password, reason: $("#password-reason").value.trim() },
            });
            $("#password-form").reset();
            $("#password-form").hidden = true;
            if (user.id === state.session.user.id) {
                $("#user-dialog").close();
                showLogin("Пароль изменён. Войдите ещё раз с новым паролем.");
            } else {
                toast("Пароль изменён, старые сессии завершены");
            }
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function clearUserProfile(event) {
        event.preventDefault();
        const user = state.activeUser;
        if (!user) return;
        const confirmation = $("#profile-clear-confirmation").value.trim();
        if (confirmation.toLocaleLowerCase() !== user.username.toLocaleLowerCase()) {
            toast("Ник для подтверждения введён неверно", "error");
            return;
        }
        if (!window.confirm(`Безвозвратно удалить все игровые данные ${user.username}? Сам аккаунт останется.`)) return;
        const button = event.submitter;
        setButtonBusy(button, true, "Очищаем…");
        try {
            const result = await api(`/users/${user.id}/clear-profile`, {
                method: "POST",
                data: {
                    confirmation,
                    reason: $("#profile-clear-reason").value.trim(),
                    reset_public_profile: $("#profile-clear-public").checked,
                },
            });
            $("#profile-clear-form").reset();
            $("#profile-clear-public").checked = true;
            $("#profile-clear-form").hidden = true;
            toast(`Профиль очищен: удалено скоров — ${formatNumber(result.deleted_scores)}`);
            await openUser(user.id);
            await loadUsers();
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    function scoreImportFormValues() {
        const file = $("#score-import-file").files[0] || null;
        return {
            source: $("#score-import-source").value.trim(),
            ruleset: $("#score-import-ruleset").value || null,
            targetUserId: Number($("#score-import-user-id").value),
            beatmapId: Number($("#score-import-beatmap-id").value) || null,
            file,
            sourceType: file ? "osr" : "official",
            allowUnverifiedRevision: $("#score-import-unsafe-revision").checked,
        };
    }

    function scoreImportFingerprint(values = scoreImportFormValues()) {
        const fileFingerprint = values.file
            ? [values.file.name, values.file.size, values.file.lastModified]
            : null;
        return JSON.stringify([
            values.sourceType,
            values.source,
            values.ruleset,
            values.targetUserId,
            values.beatmapId,
            fileFingerprint,
            values.allowUnverifiedRevision,
        ]);
    }

    function invalidateScoreImportPreview() {
        state.scoreImportPreview = null;
        $("#score-import-preview").hidden = true;
        $("#score-import-preview").replaceChildren();
        $("#score-import-submit").disabled = true;
    }

    function scoreMods(mods) {
        if (!Array.isArray(mods) || !mods.length) return "NM";
        const names = mods
            .map((mod) => typeof mod === "string" ? mod : mod?.acronym)
            .filter(Boolean)
            .map((mod) => String(mod).toUpperCase());
        return names.join("") || "NM";
    }

    function officialScoreUrl(value) {
        if (!value) return null;
        try {
            const url = new URL(value);
            if (url.protocol !== "https:" || !["osu.ppy.sh", "www.osu.ppy.sh"].includes(url.hostname)) return null;
            if (!url.pathname.startsWith("/scores/")) return null;
            return url.href;
        } catch {
            return null;
        }
    }

    function importMetric(label, value) {
        const item = make("div", "import-metric");
        item.append(make("span", "", label), make("strong", "", value));
        return item;
    }

    function scoreImportWarning(message) {
        const translations = {
            "The official score does not include a verifiable beatmap checksum": "Официальный score не содержит контрольную сумму карты, поэтому безопасный перенос невозможен.",
            "The score belongs to an older beatmap revision": "Score сыгран на старой версии карты. В текущий рейтинг его переносить небезопасно.",
            "The score response does not include a current beatmap metadata checksum": "Ответ osu! не содержит контрольную сумму текущей карты.",
            "The score response references different current beatmap metadata": "Метаданные score отличаются от текущей версии карты.",
            "The official score has no replay, so its played beatmap revision cannot be verified": "У score нет replay: подтвердить сыгранную версию карты невозможно.",
            "The official replay could not be downloaded, so its beatmap revision is unverified": "Replay недоступен для скачивания: версия карты не подтверждена.",
            "The official replay exceeds the 32 MiB safety limit": "Replay больше безопасного лимита 32 МиБ и не был загружен.",
            "The official replay is empty or invalid and cannot verify the beatmap revision": "Replay пуст или повреждён и не подтверждает версию карты.",
            "The replay ruleset does not match the official score": "Режим игры в replay не совпадает с режимом score.",
            "The replay was played on a different beatmap revision": "Replay сыгран на другой версии карты.",
            "Owner override enabled: normal local ranking rules will use the current map revision": "Включён рискованный режим: рейтинг и PP будут рассчитаны по текущей карте.",
            "The score will stay visible, but will not enter leaderboards or award PP": "Score будет виден в профиле, но останется вне лидерборда и без PP.",
            "This official score has already been imported": "Этот официальный score уже был перенесён на сервер.",
        };
        return translations[message] || message;
    }

    function replayVerificationName(status) {
        const labels = {
            verified: "Replay подтверждает текущую карту",
            not_advertised: "У score нет replay",
            unavailable: "Replay недоступен",
            too_large: "Replay превышает лимит",
            empty: "Replay пуст",
            invalid: "Replay повреждён",
            ruleset_mismatch: "В replay другой режим",
            checksum_mismatch: "Replay от другой версии карты",
        };
        return labels[status] || "Ревизия карты не подтверждена";
    }

    function renderScoreImportPreview(score, target, checks = {}, canImport = true) {
        const root = $("#score-import-preview");
        root.replaceChildren();

        const head = make("div", "import-preview-head");
        const gradeName = String(score.rank || "");
        const grade = make("span", `import-grade${gradeName.startsWith("S") || gradeName.startsWith("X") ? " is-gold" : ""}`, score.rank || "—");
        const copy = make("div", "import-preview-copy");
        copy.append(
            make("h4", "", `${score.artist || "Неизвестный артист"} — ${score.title || "Неизвестная карта"}`),
            make("p", "", `[${score.version || "неизвестная сложность"}] · score #${score.source_score_id}`),
        );
        head.append(grade, copy, make("span", "tag is-ranker", score.ruleset || "osu"));

        const metrics = make("div", "import-metrics");
        metrics.append(
            importMetric(score.source === "uploaded_osr" ? "PP после импорта" : "Официальный PP", score.pp_official === null || score.pp_official === undefined ? "—" : `${formatNumber(score.pp_official)} pp`),
            importMetric("Точность", formatAccuracy(score.accuracy)),
            importMetric("Score", formatNumber(score.total_score)),
            importMetric("Комбо", `${formatNumber(score.max_combo)}x`),
        );

        const sourceLine = make("div", "import-source-line");
        const sourceCopy = make("span", "", `${score.source_username || "Неизвестный игрок osu!"} · ${scoreMods(score.mods)} · ${formatDate(score.ended_at)}`);
        const url = officialScoreUrl(score.source_url);
        sourceLine.append(sourceCopy);
        if (url) {
            const link = make("a", "text-button", "Открыть на osu! ↗");
            link.href = url;
            link.target = "_blank";
            link.rel = "noopener noreferrer";
            sourceLine.append(link);
        }

        const targetLine = make("div", "import-target-line");
        const avatar = make("span", "avatar avatar-small", initials(target.username));
        const targetCopy = make("span");
        targetCopy.append(make("small", "", "Получатель на этом сервере"), make("strong", "", `${target.username} · ID #${target.server_id || target.id}`));
        targetLine.append(avatar, targetCopy, make("span", `status-badge ${target.is_active && !target.is_restricted ? "is-ok" : "is-warn"}`, target.is_active && !target.is_restricted ? "Можно импортировать" : "Проверьте аккаунт"));

        const replay = make("p", "import-preview-note", score.source === "uploaded_osr"
            ? "Файл replay будет сохранён вместе со score, а имя внутри копии заменится на ник выбранного локального игрока."
            : score.has_replay
                ? "У официального результата есть replay — сервер попробует перенести его вместе со score."
                : "У официального результата нет доступного replay; сам score всё равно можно перенести.");
        const revisionVerified = checks.revision_verified === true;
        const normalRanking = checks.ranking_outcome === "normal";
        const verification = make("div", `import-verification ${revisionVerified ? "is-ok" : normalRanking ? "is-risk" : "is-safe"}`);
        const verificationCopy = make("span");
        verificationCopy.append(
            make("strong", "", replayVerificationName(checks.replay_status)),
            make(
                "small",
                "",
                revisionVerified
                    ? "Score получит обычный локальный рейтинг и расчёт PP."
                    : normalRanking
                        ? "Подтверждения нет, но владелец принудительно включил лидерборд и PP."
                        : "Безопасный режим: score будет только в истории профиля, без лидерборда и PP.",
            ),
        );
        verification.append(
            make("i", "", revisionVerified ? "✓" : normalRanking ? "!" : "○"),
            verificationCopy,
            make("span", "tag", normalRanking ? "Рейтинг + PP" : "Вне рейтинга"),
        );
        const warnings = Array.isArray(checks.warnings) ? checks.warnings : [];
        const warningList = make("div", "import-warning-list");
        warnings.forEach((warning) => warningList.append(make("p", "import-preview-warning", scoreImportWarning(warning))));
        if (!canImport && !warnings.length) {
            warningList.append(make("p", "import-preview-warning", "Этот score сейчас нельзя импортировать."));
        }
        root.append(head, metrics, sourceLine, targetLine, replay, verification);
        if (warningList.childElementCount) root.append(warningList);
        root.hidden = false;
    }

    async function previewScoreImport() {
        const values = scoreImportFormValues();
        if (!values.source && !values.file) {
            toast("Вставьте ссылку на score или выберите файл .osr", "error");
            $("#score-import-source").focus();
            return;
        }
        if (values.file && values.file.size > 32 * 1024 * 1024) {
            toast("Файл replay больше допустимых 32 МиБ", "error");
            return;
        }
        if (!Number.isInteger(values.targetUserId) || values.targetUserId <= 0) {
            $("#score-import-user-id").reportValidity();
            return;
        }
        const button = $("#score-import-preview-button");
        setButtonBusy(button, true, "Проверяем…");
        invalidateScoreImportPreview();
        try {
            let previewRequest;
            if (values.sourceType === "osr") {
                const form = new FormData();
                form.append("replay", values.file, values.file.name);
                form.append("allow_unverified_revision", String(values.allowUnverifiedRevision));
                if (values.beatmapId) form.append("beatmap_id", String(values.beatmapId));
                previewRequest = api("/score-imports/osr/preview", { method: "POST", form });
            } else {
                previewRequest = api("/score-imports/preview", {
                    method: "POST",
                    data: {
                        source: values.source,
                        ruleset: values.ruleset,
                        allow_unverified_revision: values.allowUnverifiedRevision,
                    },
                });
            }
            const [preview, target] = await Promise.all([
                previewRequest,
                api(`/users/${values.targetUserId}`),
            ]);
            state.scoreImportPreview = {
                fingerprint: scoreImportFingerprint(values),
                file: values.file,
                score: preview.score,
                target,
                checks: preview.checks || {},
                canImport: preview.can_import !== false,
            };
            renderScoreImportPreview(preview.score, target, preview.checks, preview.can_import !== false);
            const targetAllowed = target.is_active && !target.is_restricted;
            const importAllowed = targetAllowed && preview.can_import !== false;
            $("#score-import-submit").disabled = !importAllowed;
            const message = !targetAllowed
                ? "Этот аккаунт нельзя использовать как получателя"
                : importAllowed
                    ? preview.checks?.revision_verified
                        ? "Score, replay и получатель проверены"
                        : preview.checks?.ranking_outcome === "normal"
                            ? "Внимание: включён рискованный импорт с рейтингом и PP"
                            : "Score можно безопасно добавить вне рейтинга"
                    : "Проверка нашла причину, по которой score нельзя импортировать";
            const messageType = importAllowed
                && (preview.checks?.revision_verified || preview.checks?.ranking_outcome !== "normal")
                ? "success"
                : "error";
            toast(message, messageType);
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function submitScoreImport(event) {
        event.preventDefault();
        const values = scoreImportFormValues();
        const preview = state.scoreImportPreview;
        if (!preview || preview.fingerprint !== scoreImportFingerprint(values) || preview.file !== values.file) {
            invalidateScoreImportPreview();
            toast("Сначала снова проверьте score и получателя", "error");
            return;
        }
        if (!preview.target.is_active || preview.target.is_restricted) {
            toast("Этот аккаунт нельзя использовать как получателя", "error");
            return;
        }
        if (!preview.canImport) {
            toast("Этот score не прошёл проверку и не может быть импортирован", "error");
            return;
        }
        const reason = $("#score-import-reason").value.trim();
        const includeReplay = $("#score-import-replay").checked;
        const unsafeOverride = values.allowUnverifiedRevision;
        const sourceLabel = values.sourceType === "osr" ? "replay" : "score";
        const question = unsafeOverride
            ? `ВНИМАНИЕ: включён рискованный режим. Импортировать ${sourceLabel} #${preview.score.source_score_id} игроку ${preview.target.username} с разрешением дать рейтинг и PP, даже если повторная проверка не подтвердит версию карты?`
            : `Импортировать ${sourceLabel} #${preview.score.source_score_id} игроку ${preview.target.username} (ID ${preview.target.id})?`;
        if (!window.confirm(question)) return;

        const button = $("#score-import-submit");
        setButtonBusy(button, true, "Импортируем…");
        try {
            let result;
            if (values.sourceType === "osr") {
                const form = new FormData();
                form.append("replay", values.file, values.file.name);
                form.append("target_user_id", String(values.targetUserId));
                form.append("reason", reason);
                form.append("allow_unverified_revision", String(values.allowUnverifiedRevision));
                if (values.beatmapId) form.append("beatmap_id", String(values.beatmapId));
                result = await api("/score-imports/osr", { method: "POST", form });
            } else {
                result = await api("/score-imports", {
                    method: "POST",
                    data: {
                        source: values.source,
                        ruleset: values.ruleset,
                        target_user_id: values.targetUserId,
                        include_replay: includeReplay,
                        allow_unverified_revision: values.allowUnverifiedRevision,
                        reason,
                    },
                });
            }
            $("#score-import-form").reset();
            $("#score-import-ruleset").disabled = false;
            $("#score-import-replay").disabled = false;
            invalidateScoreImportPreview();
            const pp = result.score?.pp ? ` · ${formatNumber(result.score.pp)} pp` : "";
            toast(`Score #${result.score?.id || "—"} импортирован${pp}`);
            if (result.replay_unavailable) toast("Score импортирован, но официальный replay недоступен", "error");
            if (result.replay_not_retained) toast("Реплей не сохранён по правилам сервера: нужен лучший успешный результат с этими настройками модов на карте с лидербордом");
            if (result.pp_pending) {
                toast(result.pp_retry_queued ? "Расчёт PP поставлен в очередь" : "Расчёт PP пока не готов — проверьте журнал сервера", result.pp_retry_queued ? "success" : "error");
            }
            await loadScoreImports();
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
            button.disabled = !state.scoreImportPreview;
        }
    }

    function createScoreImportCard(item) {
        const score = item.score || {};
        const provenance = item.provenance || {};
        const target = provenance.target_user || {};
        const importer = provenance.imported_by || {};
        const card = make("article", "import-card");

        const head = make("div", "import-card-head");
        const gradeName = String(score.rank || "");
        const grade = make("span", `import-grade${gradeName.startsWith("S") || gradeName.startsWith("X") ? " is-gold" : ""}`, score.rank || "—");
        const title = make("div", "import-card-title");
        const sourceTitle = provenance.source === "uploaded_osr" ? "Файл replay" : "Официальный score";
        title.append(
            make("h4", "", `${sourceTitle} #${provenance.source_score_id || "—"}`),
            make("p", "", `${provenance.source_username || "Неизвестный игрок osu!"} · ${formatDate(provenance.imported_at)}`),
        );
        head.append(grade, title, make("span", "tag is-ranker", score.ruleset || provenance.source_ruleset || "osu"));

        const targetLine = make("button", "import-history-target");
        targetLine.type = "button";
        targetLine.append(
            make("span", "avatar avatar-small", initials(target.username)),
            make("span", "", `→ ${target.username || `Игрок #${target.id || "—"}`}`),
            make("small", "", `локальный score #${score.id || provenance.score_id || "—"}`),
        );
        if (target.id) targetLine.addEventListener("click", () => { navigate("users"); openUser(target.id); });
        else targetLine.disabled = true;

        const metrics = make("div", "import-card-metrics");
        metrics.append(
            importMetric("PP", `${formatNumber(score.pp)} pp`),
            importMetric("Точность", formatAccuracy(score.accuracy)),
            importMetric("Score", formatNumber(score.total_score)),
            importMetric("Mods", scoreMods(score.mods)),
        );

        const flags = make("div", "policy-flags");
        const verification = provenance.revision_verification || {};
        flags.append(
            make("span", "tag", score.has_replay ? "Replay ✓" : "Без replay"),
            make("span", `tag ${verification.revision_verified ? "is-ranker" : verification.unverified_override_used ? "is-owner" : ""}`, verification.revision_verified ? "Ревизия ✓" : verification.unverified_override_used ? "Ревизия: override" : "Ревизия не подтверждена"),
            make("span", "tag", score.leaderboard_eligible ? "Лидерборд ✓" : "Вне лидерборда"),
            make("span", "tag", score.ranked ? "Ranked" : "Не ranked"),
        );

        const footer = make("div", "import-card-footer");
        const reason = make("span", "import-reason", provenance.reason || "Причина не указана");
        reason.title = provenance.reason || "";
        const sourceUrl = officialScoreUrl(provenance.source_url);
        footer.append(reason);
        if (sourceUrl) {
            const source = make("a", "text-button", "Источник ↗");
            source.href = sourceUrl;
            source.target = "_blank";
            source.rel = "noopener noreferrer";
            footer.append(source);
        }
        if (importer.username) footer.append(make("small", "", `Импортировал: ${importer.username}`));
        card.append(head, targetLine, metrics, flags, footer);
        return card;
    }

    async function loadScoreImports() {
        const data = await api("/score-imports?limit=100&offset=0");
        const items = data.items || [];
        $("#score-import-count").textContent = formatNumber(data.total ?? items.length);
        $("#score-import-list").replaceChildren(...items.map(createScoreImportCard));
        $("#score-import-empty").hidden = items.length > 0;
    }

    function rankingStatusName(status) {
        const labels = { "-2": "Graveyard", "-1": "WIP", 0: "Pending", 1: "Ranked", 2: "Approved", 3: "Qualified", 4: "Loved" };
        return labels[Number(status)] || `Статус ${status}`;
    }

    function policySourceName(source) {
        const labels = {
            upstream: "Bancho",
            beatmapset: "локальное правило сета",
            beatmap: "локальное правило сложности",
            local_set: "локальное правило сета",
            local_difficulty: "локальное правило сложности",
        };
        return labels[source] || String(source || "неизвестно");
    }

    function renderRankingPreview(data) {
        state.rankingSet = data;
        const root = $("#ranking-preview");
        root.replaceChildren();
        const beatmapset = data.beatmapset;
        const head = make("div", "ranking-preview-head");
        const copy = make("div");
        copy.append(
            make("h4", "", `${beatmapset.artist} — ${beatmapset.title}`),
            make("p", "", `mapper: ${beatmapset.creator} · официальный: ${rankingStatusName(beatmapset.upstream_status)}`),
        );
        head.append(copy, make("span", "tag is-ranker", `${rankingStatusName(beatmapset.effective.status)} · ${policySourceName(beatmapset.effective.source)}`));
        root.append(head);

        const list = make("div", "difficulty-list");
        (data.difficulties || []).forEach((difficulty) => {
            const button = make("button", "difficulty-button");
            button.type = "button";
            const name = make("span", "", difficulty.version || `Сложность #${difficulty.id}`);
            const details = make("small", "", `${Number(difficulty.stars || 0).toFixed(2)}★ · ${rankingStatusName(difficulty.effective.status)}${difficulty.pending_event_count ? ` · ⚠ ${difficulty.pending_event_count}` : ""}`);
            button.append(name, details);
            button.title = `Выбрать сложность #${difficulty.id}`;
            button.addEventListener("click", () => {
                $('input[name="scope"][value="difficulty"]').checked = true;
                $("#ranking-map-id").value = difficulty.id;
                updateRankingForm();
                toast(`Выбрана сложность «${difficulty.version}»`);
            });
            list.append(button);
        });
        root.append(list);
        root.hidden = false;
    }

    async function inspectRankingSet(refresh = true) {
        const beatmapsetId = Number($("#ranking-set-id").value);
        if (!beatmapsetId) {
            toast("Сначала введите ID сета карты", "error");
            $("#ranking-set-id").focus();
            return;
        }
        const button = $("#ranking-inspect");
        setButtonBusy(button, true, refresh ? "Получаем…" : "Загрузка…");
        try {
            const data = await api(`/ranking/beatmapsets/${beatmapsetId}${refresh ? "/refresh" : ""}`, {
                method: refresh ? "POST" : "GET",
            });
            renderRankingPreview(data);
            if (refresh) toast("Свежие данные карты получены");
        } catch (error) {
            state.rankingSet = null;
            $("#ranking-preview").hidden = true;
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    function updateRankingForm() {
        const action = $('input[name="action"]:checked').value;
        const scope = $('input[name="scope"]:checked').value;
        const rank = action === "rank";
        $("#ranking-map-field").hidden = scope !== "difficulty";
        $("#ranking-map-id").required = scope === "difficulty";
        $("#ranking-options").hidden = !rank;
        $("#ranking-submit").textContent = action === "rank"
            ? "Применить локальный ранг"
            : action === "unrank"
                ? "Отключить ranked локально"
                : "Вернуться к статусу Bancho";

        const outcome = $("#ranking-outcome");
        outcome.replaceChildren();
        if (action === "inherit") return;
        const ranked = rank && Number($("#ranking-status").value) === 1;
        [["Таблица лидеров", rank], ["Начисление PP", ranked]].forEach(([label, enabled]) => {
            const item = make("div", "outcome-item");
            item.append(make("span", `outcome-dot${enabled ? " is-on" : ""}`));
            const copy = make("span");
            copy.append(make("strong", "", enabled ? "Включено" : "Выключено"), document.createTextNode(label));
            item.append(copy);
            outcome.append(item);
        });
    }

    async function submitRanking(event) {
        event.preventDefault();
        const action = $('input[name="action"]:checked').value;
        const scope = $('input[name="scope"]:checked').value;
        const status = Number($("#ranking-status").value);
        const payload = {
            action,
            beatmapset_id: Number($("#ranking-set-id").value),
            beatmap_id: scope === "difficulty" ? Number($("#ranking-map-id").value) : null,
            status,
            leaderboard_enabled: action === "rank" ? true : null,
            pp_enabled: action === "rank" ? status === 1 : null,
            reason: $("#ranking-reason").value.trim(),
        };
        const actionText = action === "rank"
            ? `выдать статус ${rankingStatusName(status)}`
            : action === "unrank"
                ? "принудительно снять ranked и отключить локальные лидерборд с PP"
                : "снять локальное правило и вернуться к официальному статусу";
        const targetText = payload.beatmap_id ? `сложности #${payload.beatmap_id}` : `всего сета #${payload.beatmapset_id}`;
        if (!window.confirm(`Точно ${actionText} для ${targetText}? Связанные записи старой версии допуска будут очищены.`)) return;

        const button = $("#ranking-submit");
        setButtonBusy(button, true, "Проверяем карту…");
        try {
            await api("/ranking", { method: "POST", data: payload });
            toast(action === "rank"
                ? "Локальный ранг применён"
                : action === "unrank"
                    ? "Ranked и PP для карты отключены локально"
                    : "Карта снова следует статусу Bancho");
            $("#ranking-reason").value = "";
            await Promise.all([loadPolicies(), loadDashboard()]);
            inspectRankingSet(false).catch(() => undefined);
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    function createPolicyCard(policy) {
        const card = make("article", "policy-card");
        const top = make("div", "policy-top");
        const heading = make("div");
        const metadata = policy.metadata;
        const name = metadata ? `${metadata.artist} — ${metadata.title}` : `Beatmapset #${policy.beatmapset_id}`;
        heading.append(make("h4", "policy-title", name));
        heading.append(make("p", "policy-meta", metadata ? `mapper: ${metadata.creator} · сет #${policy.beatmapset_id}` : `сет #${policy.beatmapset_id}`));
        const status = make("span", `tag ${Number(policy.status) === 4 ? "is-ranker" : "is-admin"}`, rankingStatusName(policy.status));
        top.append(heading, status);

        const flags = make("div", "policy-flags");
        flags.append(make("span", "tag", policy.scope === "beatmap" ? `Сложность #${policy.beatmap_id}` : "Весь сет"));
        flags.append(make("span", "tag", policy.leaderboard_enabled ? "Лидерборд ✓" : "Лидерборд —"));
        flags.append(make("span", "tag", policy.pp_enabled ? "PP ✓" : "PP —"));
        if (policy.force_unranked) flags.append(make("span", "tag is-owner", "Принудительный unrank"));
        if (policy.blocks_set_policy) flags.append(make("span", "tag is-owner", "Перекрывает сет"));

        const footer = make("div", "policy-footer");
        const reason = make("span", "policy-reason", policy.reason || "Причина не указана");
        reason.title = policy.reason || "";
        const remove = make("button", "row-action", "Снять правило");
        remove.type = "button";
        remove.addEventListener("click", () => prepareInherit(policy));
        footer.append(reason, remove);
        card.append(top, flags, footer);
        return card;
    }

    async function loadPolicies() {
        const data = await api("/ranking/policies?limit=200");
        const policies = [...data.beatmapsets, ...data.difficulties].sort((a, b) => new Date(b.updated_at) - new Date(a.updated_at));
        $("#policies-count").textContent = String(policies.length);
        const list = $("#policies-list");
        list.replaceChildren(...policies.map(createPolicyCard));
        $("#policies-empty").hidden = policies.length > 0;
    }

    function prepareInherit(policy) {
        $('input[name="action"][value="inherit"]').checked = true;
        const scope = policy.scope === "beatmap" ? "difficulty" : "set";
        $(`input[name="scope"][value="${scope}"]`).checked = true;
        $("#ranking-set-id").value = policy.beatmapset_id;
        $("#ranking-map-id").value = policy.beatmap_id || "";
        $("#ranking-reason").value = "Возврат к официальному статусу";
        updateRankingForm();
        inspectRankingSet(false).catch(() => undefined);
        $("#ranking-form").scrollIntoView({ behavior: "smooth", block: "center" });
        $("#ranking-reason").focus({ preventScroll: true });
    }

    function eventLabel(event) {
        const labels = {
            upstream_revision_changed: "Изменилась версия карты на Bancho",
            beatmap_updated: "Карта обновлена",
            checksum_changed: "Изменилась версия карты",
            official_status_changed: "Изменился статус Bancho",
            local_rank_invalidated: "Локальный ранг требует проверки",
        };
        return labels[event.event_type] || String(event.event_type || "Событие карты").replaceAll("_", " ");
    }

    function createEventCard(event) {
        const card = make("article", `event-card${event.resolved_at ? " is-resolved" : ""}`);
        const top = make("div", "event-top");
        const heading = make("div");
        heading.append(make("h3", "event-title", eventLabel(event)), make("p", "event-meta", `Сет #${event.beatmapset_id}${event.beatmap_id ? ` · сложность #${event.beatmap_id}` : " · весь сет"}`));
        top.append(heading, make("span", `status-badge ${event.resolved_at ? "is-ok" : "is-warn"}`, event.resolved_at ? "Закрыто" : "Новое"));
        const body = make("div", "event-body", event.reason || "Сервер обнаружил изменение официальной версии карты.");
        const footer = make("div", "event-footer");
        footer.append(make("time", "", formatDate(event.created_at)));
        if (!event.resolved_at) {
            const resolve = make("button", "row-action", "Отметить просмотренным");
            resolve.type = "button";
            resolve.addEventListener("click", () => openResolveDialog(event.id));
            footer.append(resolve);
        } else {
            footer.append(make("span", "", `закрыто ${formatDate(event.resolved_at)}`));
        }
        card.append(top, body, footer);
        return card;
    }

    async function loadEvents() {
        const includeResolved = $("#include-resolved").checked;
        const data = await api(`/ranking/events?include_resolved=${includeResolved}&limit=100`);
        const list = $("#events-list");
        list.replaceChildren(...data.items.map(createEventCard));
        $("#events-empty").hidden = data.items.length > 0;
        const pending = data.items.filter((item) => !item.resolved_at).length;
        const badge = $("#nav-inbox-badge");
        badge.textContent = pending > 99 ? "99+" : String(pending);
        badge.hidden = pending === 0;
    }

    function openResolveDialog(eventId) {
        $("#resolve-event-id").value = eventId;
        $("#resolve-reason").value = "Проверено в админ-панели";
        $("#resolve-dialog").showModal();
    }

    async function resolveEvent(event) {
        event.preventDefault();
        const button = event.submitter;
        setButtonBusy(button, true, "Закрываем…");
        try {
            await api(`/ranking/events/${$("#resolve-event-id").value}/resolve`, {
                method: "POST",
                data: { reason: $("#resolve-reason").value.trim() },
            });
            $("#resolve-dialog").close();
            toast("Уведомление закрыто");
            await Promise.all([loadEvents(), loadDashboard()]);
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function loadAudit() {
        state.audit = await api("/audit?limit=200");
        renderAudit();
    }

    function setAuditHeaders(labels) {
        const row = make("tr");
        labels.forEach((label) => row.append(make("th", "", label)));
        $("#audit-table-head").replaceChildren(row);
    }

    function shortJson(value) {
        if (value === null || value === undefined) return "—";
        try { return JSON.stringify(value); } catch { return "—"; }
    }

    function renderAudit() {
        if (!state.audit) return;
        const body = $("#audit-table-body");
        body.replaceChildren();
        const items = state.audit[state.auditTab] || [];
        if (state.auditTab === "account") {
            setAuditHeaders(["Когда", "Кто", "Действие", "Цель", "Причина", "Изменения", "IP"]);
            items.forEach((item) => {
                const row = make("tr");
                const changes = shortJson({ before: item.before, after: item.after });
                const changesCell = make("td");
                const code = make("div", "audit-json", changes);
                code.title = changes;
                changesCell.append(code);
                row.append(
                    make("td", "muted", formatDate(item.created_at)),
                    make("td", "", item.actor_username || `ID ${item.actor_user_id || "—"}`),
                    make("td", "", item.action),
                    make("td", "", `${item.target_type} #${item.target_id}`),
                    make("td", "", item.reason || "—"),
                    changesCell,
                    make("td", "muted", item.ip_address || "—"),
                );
                body.append(row);
            });
        } else {
            setAuditHeaders(["Когда", "Кто", "Действие", "Карта", "Причина", "Изменения"]);
            items.forEach((item) => {
                const row = make("tr");
                const changes = shortJson({ before: item.before, after: item.after });
                const changesCell = make("td");
                const code = make("div", "audit-json", changes);
                code.title = changes;
                changesCell.append(code);
                row.append(
                    make("td", "muted", formatDate(item.created_at)),
                    make("td", "", `ID ${item.actor_user_id}`),
                    make("td", "", item.action),
                    make("td", "", `Сет #${item.beatmapset_id}${item.beatmap_id ? ` · ${item.beatmap_id}` : ""}`),
                    make("td", "", item.reason || "—"),
                    changesCell,
                );
                body.append(row);
            });
        }
        $("#audit-empty").hidden = items.length > 0;
    }

    function serviceCard(name, note, ok) {
        const card = make("article", "service-card");
        const copy = make("div");
        copy.append(make("strong", "", name), make("small", "", note));
        card.append(copy, make("span", `service-light${ok ? " is-ok" : ""}`));
        return card;
    }

    function settingRow(label, value) {
        const wrapper = make("div", "setting-row");
        wrapper.append(make("dt", "", label), make("dd", "", value));
        return wrapper;
    }

    async function loadSystem() {
        const data = await api("/dashboard");
        state.dashboard = data;
        const system = data.system;
        $("#system-health").replaceChildren(
            serviceCard("API", "Игровые и административные запросы", system.api),
            serviceCard("MySQL", "Аккаунты, результаты и карты", system.database),
            serviceCard("Redis", "Кэш и защищённые сессии", system.redis),
        );
        $("#system-settings").replaceChildren(
            settingRow("Адрес сервера", system.server_url),
            settingRow("Время работы", formatDuration(system.uptime_seconds)),
            settingRow("Режим подсчёта", system.scoring_mode),
            settingRow("Страна новых аккаунтов", `${flagEmoji(system.default_country_code)} ${system.default_country_code}`),
            settingRow("Автосинхронизация карт", system.auto_beatmap_sync ? "Включена" : "Выключена"),
            settingRow("PP для всех карт", system.all_beatmap_pp ? "Включено" : "Только допущенные карты"),
            settingRow("Лидерборд для всех карт", system.all_beatmap_leaderboard ? "Включён" : "Только допущенные карты"),
        );
    }

    function bindEvents() {
        $("#login-form").addEventListener("submit", handleLogin);
        $("#logout-button").addEventListener("click", logout);
        $("#refresh-button").addEventListener("click", refreshCurrent);
        $("#profile-button").addEventListener("click", () => {
            if (hasCapability("administrator")) {
                navigate("users");
                openUser(state.session.user.id);
            }
        });
        $$('[data-view]').forEach((button) => button.addEventListener("click", () => navigate(button.dataset.view)));
        $$('[data-go]').forEach((button) => button.addEventListener("click", () => navigate(button.dataset.go)));

        let searchTimer;
        $("#user-search").addEventListener("input", (event) => {
            window.clearTimeout(searchTimer);
            searchTimer = window.setTimeout(() => {
                state.users.query = event.target.value.trim();
                state.users.page = 1;
                if (state.currentView === "users") refreshCurrent();
            }, 320);
        });
        $("#users-prev").addEventListener("click", () => {
            if (state.users.page > 1) { state.users.page -= 1; refreshCurrent(); }
        });
        $("#users-next").addEventListener("click", () => {
            if (state.users.page < state.users.pages) { state.users.page += 1; refreshCurrent(); }
        });

        $$('[data-user-tab]').forEach((button) => button.addEventListener("click", () => selectUserTab(button.dataset.userTab)));
        $("#user-edit-form").addEventListener("submit", saveUser);
        $("#somsai-mmr-pool").addEventListener("change", updateSelectedSomsaiMmr);
        $("#somsai-mmr-form").addEventListener("submit", saveSomsaiMmr);
        $("#somsai-mmr-refresh").addEventListener("click", refreshSomsaiMmr);
        $("#password-open").addEventListener("click", () => {
            $("#password-form").hidden = false;
            $("#new-password").focus();
        });
        $("#password-cancel").addEventListener("click", () => {
            $("#password-form").reset();
            $("#password-form").hidden = true;
        });
        $("#password-form").addEventListener("submit", resetPassword);
        $("#profile-clear-open").addEventListener("click", () => {
            $("#profile-clear-form").hidden = false;
            $("#profile-clear-reason").focus();
        });
        $("#profile-clear-cancel").addEventListener("click", () => {
            $("#profile-clear-form").reset();
            $("#profile-clear-public").checked = true;
            $("#profile-clear-form").hidden = true;
        });
        $("#profile-clear-form").addEventListener("submit", clearUserProfile);
        $("#user-dialog").addEventListener("close", () => { state.activeUser = null; });

        $("#score-import-preview-button").addEventListener("click", previewScoreImport);
        $("#score-import-form").addEventListener("submit", submitScoreImport);
        ["#score-import-user-id", "#score-import-beatmap-id"].forEach((selector) => {
            $(selector).addEventListener("input", invalidateScoreImportPreview);
        });
        $("#score-import-source").addEventListener("input", () => {
            if ($("#score-import-source").value.trim()) $("#score-import-file").value = "";
            $("#score-import-ruleset").disabled = false;
            $("#score-import-replay").disabled = false;
            invalidateScoreImportPreview();
        });
        $("#score-import-file").addEventListener("change", () => {
            const hasFile = Boolean($("#score-import-file").files.length);
            if (hasFile) $("#score-import-source").value = "";
            $("#score-import-ruleset").disabled = hasFile;
            $("#score-import-replay").checked = true;
            $("#score-import-replay").disabled = hasFile;
            invalidateScoreImportPreview();
        });
        $("#score-import-ruleset").addEventListener("change", invalidateScoreImportPreview);
        $("#score-import-unsafe-revision").addEventListener("change", invalidateScoreImportPreview);

        $$('input[name="action"], input[name="scope"]').forEach((input) => input.addEventListener("change", updateRankingForm));
        $("#ranking-status").addEventListener("change", updateRankingForm);
        $("#ranking-inspect").addEventListener("click", () => inspectRankingSet(true));
        $("#ranking-set-id").addEventListener("input", () => {
            if (state.rankingSet && Number($("#ranking-set-id").value) !== Number(state.rankingSet.beatmapset.id)) {
                state.rankingSet = null;
                $("#ranking-preview").hidden = true;
            }
        });
        $("#ranking-form").addEventListener("submit", submitRanking);
        for (const selector of ["#negative-pp-kind", "#negative-pp-reference"]) {
            $(selector).addEventListener("input", () => {
                state.negativePPTarget = null;
                $("#negative-pp-add").disabled = true;
                $("#negative-pp-target").textContent = "Сначала найдите карту или маппера.";
            });
        }
        $("#negative-pp-preview").addEventListener("click", () => negativePPAction(async () => {
            state.negativePPTarget = null;
            const target = await api("/negative-pp/preview", { method: "POST", data: {
                kind: $("#negative-pp-kind").value, reference: $("#negative-pp-reference").value.trim(),
            } });
            state.negativePPTarget = target;
            $("#negative-pp-target").textContent = `${target.label} · ID ${target.target_id}`;
            $("#negative-pp-status").textContent = "Цель найдена. Добавление применится к старым и будущим результатам.";
        }));
        $("#negative-pp-form").addEventListener("submit", (event) => {
            event.preventDefault();
            const target = state.negativePPTarget;
            if (!target) return;
            negativePPAction(async () => {
                const result = await api("/negative-pp", { method: "POST", data: {
                    kind: target.kind, reference: String(target.target_id), reason: $("#negative-pp-reason").value.trim(),
                } });
                state.negativePPTarget = null;
                $("#negative-pp-status").textContent = `Добавлено: ${result.rule.label}. Карт: ${result.beatmaps}; пересчитано профилей по режимам: ${result.user_modes}.`;
                await loadNegativePP();
            });
        });
        $("#include-resolved").addEventListener("change", () => {
            if (state.currentView === "inbox") refreshCurrent();
        });
        $("#resolve-form").addEventListener("submit", resolveEvent);
        $("#resolve-cancel").addEventListener("click", () => $("#resolve-dialog").close());

        $$('[data-audit-tab]').forEach((button) => button.addEventListener("click", () => {
            state.auditTab = button.dataset.auditTab;
            $$('[data-audit-tab]').forEach((tab) => {
                const active = tab === button;
                tab.classList.toggle("is-active", active);
                tab.setAttribute("aria-selected", String(active));
            });
            renderAudit();
        }));
    }

    async function start() {
        bindEvents();
        $("#somsai-select").addEventListener("change", openSomsai);
        $("#somsai-new").addEventListener("click", () => { $("#somsai-select").value = ""; openSomsai(); });
        $("#somsai-check").addEventListener("click", () => somsaiAction(async () => renderSomsaiCheck(await api("/somsai/preview", { method: "POST", data: somsaiSpec() }))));
        $("#somsai-form").addEventListener("submit", event => {
            event.preventDefault();
            somsaiAction(async () => {
                const selected = state.somsaiSelected;
                const saved = await api(selected ? `/somsai/pools/${selected.id}` : "/somsai/pools", {
                    method: selected ? "PATCH" : "POST", data: { ...somsaiSpec(), expected_revision: selected?.revision || null,
                        reason: $("#somsai-reason").value.trim() } });
                await loadSomsaiLegacy(saved.id);
                toast("Турнирный пул сохранён");
            });
        });
        $("#somsai-delete").addEventListener("click", () => {
            const selected = state.somsaiSelected;
            if (!selected || !$("#somsai-reason").reportValidity() || !confirm(`Удалить пул «${selected.name}»? Текущие матчи сохранят свои карты.`)) return;
            somsaiAction(async () => {
                await api(`/somsai/pools/${selected.id}`, { method: "DELETE", data: { expected_revision: selected.revision, reason: $("#somsai-reason").value.trim() } });
                await loadSomsaiLegacy(0);
                toast("Пул удалён");
            });
        });
        $("#somsai-import-url").addEventListener("input", () => { $("#somsai-import-round").replaceChildren(new Option("Сначала прочитайте источник", "")); invalidateSomsaiImport(); });
        $("#somsai-import-round").addEventListener("change", invalidateSomsaiImport);
        $("#somsai-import-category").addEventListener("change", invalidateSomsaiImport);
        $("#somsai-import-preview").addEventListener("click", readSomsaiSource);
        $("#somsai-import-create").addEventListener("click", () => importSomsai(false));
        $("#somsai-import-append").addEventListener("click", () => importSomsai(true));
        $$('[data-somsai-page]').forEach(button => button.addEventListener("click", () => showSomsaiPage(button.dataset.somsaiPage)));
        $$('[data-somsai-category]').forEach(button => button.addEventListener("click", () => {
            state.somsaiMapPage = 1;
            renderSomsaiSlots(button.dataset.somsaiCategory);
            loadSomsaiMaps().catch(error => toast(error.message, "error"));
        }));
        $("#somsai-maps-prev").addEventListener("click", () => { state.somsaiMapPage--; loadSomsaiMaps(); });
        $("#somsai-maps-next").addEventListener("click", () => { state.somsaiMapPage++; loadSomsaiMaps(); });
        $("#somsai-map-form").addEventListener("submit", event => {
            event.preventDefault();
            somsaiAction(async () => {
                const saved = await api("/somsai/maps", { method: "POST", data: {
                    slot: $("#somsai-map-slot").value, url: $("#somsai-map-url").value.trim(), reason: "no reason",
                } });
                state.somsaiCategory = saved.category; state.somsaiSlot = saved.slot; state.somsaiMapPage = 1;
                renderSomsaiSlots(saved.category); showSomsaiPage("storage"); await loadSomsaiMaps();
                $("#somsai-map-form").reset(); toast("Карта добавлена и проверена через osu! API");
            });
        });
        $("#somsai-warehouse-read").addEventListener("click", () => somsaiAction(async () => {
            const url = $("#somsai-warehouse-import-url").value.trim();
            const result = await api("/somsai/import/preview", { method: "POST", data: { url, round: null, category: "NM" } });
            state.somsaiImportPreview = result;
            $("#somsai-warehouse-import-round").replaceChildren(new Option("Все раунды", "__all__"), ...result.rounds.map(round => new Option(round, round)));
            $("#somsai-warehouse-import").disabled = false;
            $("#somsai-warehouse-import-result").replaceChildren(make("p", "muted", result.rounds.length ? `Найдено раундов: ${result.rounds.length}` : "Коллекция готова к импорту"));
        }));
        $("#somsai-warehouse-import").addEventListener("click", () => somsaiAction(async () => {
            const result = await api("/somsai/warehouse/import", { method: "POST", data: {
                url: $("#somsai-warehouse-import-url").value.trim(), round: $("#somsai-warehouse-import-round").value, reason: "no reason",
            } });
            renderWarehouseImport(result);
            window.setTimeout(pollWarehouseImport, 500);
        }));
        $("#somsai-refresh").addEventListener("click", () => somsaiAction(async () => {
            renderSomsaiRefresh(await api("/somsai/warehouse/refresh", { method: "POST" }));
            toast("Полное обновление хранилища запущено");
        }));
        updateRankingForm();
        try {
            const session = await api("/session");
            showApp(session);
        } catch (error) {
            showLogin(error.status === 0 ? error.message : "");
        }
    }

    start();
})();
