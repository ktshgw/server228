(() => {
    "use strict";

    const API_ROOT = "/api/private/web-site";
    const USERPAGE_MAX_LENGTH = 60_000;
    const modes = [
        { value: "osu", label: "osu!", short: "o" },
        { value: "taiko", label: "taiko", short: "t" },
        { value: "fruits", label: "catch", short: "c" },
        { value: "mania", label: "mania", short: "m" },
        { value: "osurx", label: "osu! Relax", short: "RX" },
        { value: "osuap", label: "osu! Autopilot", short: "AP" },
    ];
    const ISO_CODES = ("AF AX AL DZ AS AD AO AI AQ AG AR AM AW AU AT AZ BS BH BD BB BY BE BZ BJ BM BT BO BQ BA BW BV BR IO BN BG BF BI CV KH CM CA KY CF TD CL CN CX CC CO KM CG CD CK CR CI HR CU CW CY CZ DK DJ DM DO EC EG SV GQ ER EE SZ ET FK FO FJ FI FR GF PF TF GA GM GE DE GH GI GR GL GD GP GU GT GG GN GW GY HT HM VA HN HK HU IS IN ID IR IQ IE IM IL IT JM JP JE JO KZ KE KI KP KR KW KG LA LV LB LS LR LY LI LT LU MO MG MW MY MV ML MT MH MQ MR MU YT MX FM MD MC MN ME MS MA MZ MM NA NR NP NL NC NZ NI NE NG NU NF MK MP NO OM PK PW PS PA PG PY PE PH PN PL PT PR QA RE RO RU RW BL SH KN LC MF PM VC WS SM ST SA SN RS SC SL SG SX SK SI SB SO ZA GS SS ES LK SD SR SJ SE CH SY TW TJ TZ TH TL TG TK TO TT TN TR TM TC TV UG UA AE GB US UM UY UZ VU VE VN VG VI WF EH YE ZM ZW").split(" ");
    const numberFormat = new Intl.NumberFormat("ru-RU");
    const compactFormat = new Intl.NumberFormat("ru-RU", { notation: "compact", maximumFractionDigits: 1 });
    const dateFormat = new Intl.DateTimeFormat("ru-RU", { dateStyle: "medium", timeStyle: "short" });
    const shortDateFormat = new Intl.DateTimeFormat("ru-RU", { day: "numeric", month: "short", year: "numeric" });
    const monthYearFormat = new Intl.DateTimeFormat("ru-RU", { month: "long", year: "numeric" });
    const shortMonthYearFormat = new Intl.DateTimeFormat("ru-RU", { month: "short", year: "2-digit" });
    const relativeTimeFormat = new Intl.RelativeTimeFormat("ru-RU", { numeric: "always" });
    const regionNames = typeof Intl.DisplayNames === "function" ? new Intl.DisplayNames(["ru"], { type: "region" }) : null;
    const MOD_CATEGORY_FALLBACKS = Object.freeze({
        EZ: "DifficultyReduction", NF: "DifficultyReduction", HT: "DifficultyReduction", DC: "DifficultyReduction",
        SD: "DifficultyIncrease", PF: "DifficultyIncrease", DT: "DifficultyIncrease", NC: "DifficultyIncrease",
        HR: "DifficultyIncrease", HD: "DifficultyIncrease", FL: "DifficultyIncrease", AC: "DifficultyIncrease",
        AT: "Automation", CN: "Automation", RX: "Automation", AP: "Automation", SO: "Automation",
    });
    const SPEED_MOD_DEFAULTS = Object.freeze({ DT: 1.5, NC: 1.5, HT: 0.75, DC: 0.75 });
    const PROFILE_ROLE_LABELS = Object.freeze({
        owner: "Владелец",
        admin: "Администратор",
        moderator: "Модератор",
        ranker: "Ранкер",
    });
    const MOD_SETTING_LABELS = Object.freeze({
        speed_change: "Скорость",
        initial_rate: "Начальная скорость",
        final_rate: "Конечная скорость",
        adjust_pitch: "Изменять высоту тона",
        minimum_accuracy: "Минимальная точность",
        accuracy_judge_mode: "Режим точности",
        restart: "Перезапуск при провале",
        retries: "Дополнительные жизни",
        fail_on_slider_tail: "Провал при промахе по концу слайдера",
        require_perfect_hits: "Требовать идеальные попадания",
        circle_size: "Размер кругов",
        approach_rate: "Скорость появления",
        drain_rate: "Скорость потери HP",
        overall_difficulty: "Точность",
        scroll_speed: "Скорость прокрутки",
        extended_limits: "Расширенные пределы",
        hard_rock_offsets: "Усложнённые паттерны",
        size_multiplier: "Размер фонарика",
        combo_based_size: "Размер зависит от комбо",
        follow_delay: "Задержка фонарика",
        seed: "Сид",
        reflection: "Оси отражения",
        direction: "Направление",
        angle_sharpness: "Резкость углов",
        metronome: "Щелчки метронома",
        inverse_muting: "Начинать без звука",
        enable_metronome: "Включить метроном",
        mute_combo_count: "Полная тишина на комбо",
        affects_hit_sounds: "Приглушать хитсаунды",
        only_fade_approach_circles: "Скрывать только круги приближения",
    });

    const state = {
        mode: "osu",
        route: "home",
        session: null,
        csrf: null,
        avatar: { file: null, preview: null, revision: 0, request: null },
        busy: 0,
        home: null,
        rankings: {
            page: 1,
            pages: 1,
            section: "world",
            sort: "performance",
            country: "",
            scope: "all",
            requestId: 0,
            controller: null,
        },
        beatmaps: {
            page: 1,
            query: "",
            status: "leaderboard",
            sort: "ranked_desc",
            cursorHistory: [null],
            nextCursor: null,
            requestId: 0,
            controller: null,
        },
        beatmapDetail: { id: null, data: null, beatmapId: null, mode: null, scorePage: 1, scorePages: 1, scoreRequestId: 0, detailRequestId: 0, scope: "global", mods: null, commentPage: 1, commentPages: 1, commentSort: "new", commentRequestId: 0 },
        beatmapCache: new Map(),
        profile: {
            id: null,
            requestId: 0,
            somsaiVariant: 4,
            somsaiFormat: "1v1",
            page: 1,
            pages: 1,
            scoreType: "best",
            scoreLimit: 5,
            firstScorePage: 1,
            user: null,
            payload: null,
            activityScores: [],
            pinnedScores: [],
            pinningSupported: true,
            friendship: null,
        },
        rankChart: { points: [], activeIndex: 0 },
        playHistoryChart: { points: [], activeIndex: 0 },
        search: { controller: null, requestId: 0 },
        settingsUser: null,
        settingsProfile: null,
        settingsTab: "profile",
        modCatalog: {},
        pendingScoreDelete: null,
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
        if (detail && detail.fields && typeof detail.fields === "object") {
            const labels = { username: "Имя", email: "Почта", password: "Пароль", country_code: "Страна" };
            const messages = Object.entries(detail.fields).flatMap(([field, errors]) =>
                (Array.isArray(errors) ? errors : [errors]).filter(Boolean).map((error) => `${labels[field] || field}: ${translateError(String(error))}`)
            );
            if (messages.length) return messages.join("; ");
        }
        if (detail && typeof detail.message === "string") return translateError(detail.message);
        if (detail && typeof detail.error === "string") return translateError(detail.error);
        if (Array.isArray(detail)) return detail.map((entry) => entry.msg || "Некорректное значение").join("; ");
        return "Сервер не смог выполнить запрос";
    }

    function translateError(message) {
        const known = {
            "Invalid credentials": "Неверное имя пользователя или пароль",
            "Invalid username or password": "Неверное имя пользователя или пароль",
            "Two-factor code required": "Введите правильный код двухфакторной защиты",
            "Too many login attempts": "Слишком много попыток. Подождите несколько минут",
            "Session required": "Сначала нужно войти",
            "Web session required": "Сначала нужно войти",
            "Session expired": "Сессия закончилась. Войдите ещё раз",
            "Invalid CSRF token": "Защитный токен устарел. Обновите страницу",
            "Invalid request origin": "Сайт открыт не с адреса этого сервера",
            "Username already exists": "Такое имя пользователя уже занято",
            "Email already exists": "Эта почта уже используется",
            "User not found": "Пользователь не найден",
            "Beatmapset not found": "Сет карты не найден",
            "Registration data is invalid": "Проверь данные регистрации",
            "Invalid email": "некорректный адрес",
            "Username is already taken": "имя уже занято",
            "Email is already taken": "адрес уже используется",
            "Username or email is already registered": "Имя или почта уже используются",
            "Website login required": "Сначала нужно войти",
            "Account is not active": "Аккаунт отключён",
            "Restricted accounts cannot edit profiles": "Аккаунт с ограничениями не может менять профиль",
            "Restricted accounts cannot change avatars": "Аккаунт с ограничениями не может менять аватар",
            "Avatar file is empty": "Файл аватара пуст",
            "Avatar must be smaller than 5 MB": "Аватар должен быть меньше 5 МБ",
            "Avatar dimensions must not exceed 2048x2048": "Размер изображения не должен превышать 2048×2048 пикселей",
            "Too many avatar uploads": "Слишком много загрузок аватара. Подождите минуту",
            "Use PNG, JPEG, GIF, or WebP": "Используй PNG, JPEG, GIF или WebP",
            "The uploaded file is not a valid image": "Выбранный файл не является изображением",
            "Beatmap search is temporarily unavailable": "Поиск карт временно недоступен",
            "Beatmap details are temporarily unavailable": "Информация о карте временно недоступна",
            "Website registration is disabled while Turnstile verification is enabled; register in osu!lazer": "Регистрация на сайте временно отключена. Создайте аккаунт через osu!lazer",
        };
        return known[message] || message;
    }

    async function api(path, options = {}) {
        const method = options.method || "GET";
        const headers = new Headers({ Accept: "application/json" });
        let body;
        if (options.form) {
            body = options.form;
        } else if (options.data !== undefined) {
            headers.set("Content-Type", "application/json");
            body = JSON.stringify(options.data);
        }
        if (state.csrf && !["GET", "HEAD", "OPTIONS"].includes(method)) headers.set("X-CSRF-Token", state.csrf);

        let response;
        try {
            response = await fetch(`${API_ROOT}${path}`, {
                method,
                headers,
                body,
                credentials: "same-origin",
                signal: options.signal,
            });
        } catch (error) {
            if (error.name === "AbortError") throw error;
            setServerOnline(false);
            throw new ApiError(0, "Нет связи с сервером. Проверьте, что он запущен");
        }
        setServerOnline(true);

        let payload = null;
        if (response.status !== 204) {
            const contentType = response.headers.get("content-type") || "";
            const responseBody = await response.text();
            if (contentType.includes("application/json") && responseBody) {
                try { payload = JSON.parse(responseBody); } catch { payload = responseBody; }
            } else payload = responseBody;
        }
        if (!response.ok) {
            const detail = payload && Object.hasOwn(payload, "detail")
                ? payload.detail
                : payload && Object.hasOwn(payload, "error")
                    ? payload.error
                    : payload;
            if (response.status === 401 && state.session && !["/session", "/register"].includes(path)) {
                clearSession();
                updateAuthUI();
                toast("Сессия закончилась. Войди ещё раз.", "error");
            }
            throw new ApiError(response.status, detail);
        }
        return payload;
    }

    function setServerOnline(online) {
        const status = $("#footer-status");
        status.classList.toggle("is-offline", !online);
        status.lastChild.textContent = online ? " status! онлайн" : " status! недоступен";
    }

    function make(tag, className, text) {
        const element = document.createElement(tag);
        if (className) element.className = className;
        if (text !== undefined && text !== null) element.textContent = String(text);
        return element;
    }

    function clearSession() {
        state.avatar.request?.abort();
        state.avatar.request = null;
        clearAvatarSelection();
        avatarStatus();
        state.session = null;
        state.csrf = null;
        state.settingsUser = null;
        state.rankings.scope = "all";
    }

    function hasSitePermission(permission) {
        return state.session?.permissions?.[permission] === true;
    }

    function setBusy(active) {
        state.busy = Math.max(0, state.busy + (active ? 1 : -1));
        $("#top-progress").hidden = state.busy === 0;
    }

    async function busy(task) {
        setBusy(true);
        try { return await (typeof task === "function" ? task() : task); } finally { setBusy(false); }
    }

    function setButtonBusy(button, active, label) {
        if (!button.dataset.originalLabel) button.dataset.originalLabel = button.textContent;
        button.disabled = active;
        button.textContent = active ? label : button.dataset.originalLabel;
    }

    function toast(message, type = "success") {
        const kind = type === "error" ? "error" : "success";
        const item = make("div", `toast is-${kind}`, message);
        item.setAttribute("role", kind === "error" ? "alert" : "status");
        const region = $("#toast-region");
        region.append(item);
        window.requestAnimationFrame(() => item.classList.add("is-visible"));
        window.setTimeout(() => {
            item.classList.remove("is-visible");
            item.classList.add("is-leaving");
            item.addEventListener("transitionend", () => item.remove(), { once: true });
            window.setTimeout(() => item.remove(), 180);
        }, 3200);
    }

    async function copyTextToClipboard(value) {
        const text = String(value || "");
        if (navigator.clipboard?.writeText && window.isSecureContext) {
            try {
                await navigator.clipboard.writeText(text);
                return;
            } catch {
                // Some browsers expose the async API but still deny it. Use the
                // selection-based fallback while the click gesture is active.
            }
        }

        const activeElement = document.activeElement;
        const field = make("textarea", "clipboard-fallback");
        field.value = text;
        field.readOnly = true;
        field.setAttribute("aria-hidden", "true");
        document.body.append(field);
        try {
            field.focus({ preventScroll: true });
            field.select();
            field.setSelectionRange(0, field.value.length);
            if (!document.execCommand("copy")) throw new Error("Clipboard copy was rejected");
        } finally {
            field.remove();
            if (activeElement instanceof HTMLElement) activeElement.focus({ preventScroll: true });
        }
    }

    function formatNumber(value) {
        return numberFormat.format(Number(value || 0));
    }

    function formatCompact(value) {
        return compactFormat.format(Number(value || 0));
    }

    function formatDate(value) {
        if (!value) return "—";
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? "—" : dateFormat.format(date);
    }

    function formatShortDate(value) {
        if (!value) return "—";
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? "—" : shortDateFormat.format(date);
    }

    function formatRelativeDate(value) {
        if (!value) return "—";
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return "—";
        const seconds = Math.round((date.getTime() - Date.now()) / 1000);
        const absoluteSeconds = Math.abs(seconds);
        const units = [
            ["year", 31_536_000],
            ["month", 2_592_000],
            ["day", 86_400],
            ["hour", 3_600],
            ["minute", 60],
        ];
        for (const [unit, size] of units) {
            if (absoluteSeconds >= size) return relativeTimeFormat.format(Math.round(seconds / size), unit);
        }
        return relativeTimeFormat.format(seconds, "second");
    }

    function formatAccuracy(value) {
        let accuracy = Number(value || 0);
        if (accuracy > 0 && accuracy <= 1) accuracy *= 100;
        return `${accuracy.toFixed(2)}%`;
    }

    function flagEmoji(code) {
        const upper = String(code || "XX").toUpperCase();
        if (!/^[A-Z]{2}$/.test(upper) || upper === "XX") return "🌐";
        return String.fromCodePoint(...[...upper].map((char) => char.charCodeAt(0) + 127397));
    }

    function initials(name) {
        return [...String(name || "?").trim()].slice(0, 2).join("").toUpperCase() || "?";
    }

    function safeAssetUrl(raw) {
        if (!raw || typeof raw !== "string") return null;
        try {
            const url = new URL(raw, location.origin);
            return ["http:", "https:"].includes(url.protocol) ? url.href : null;
        } catch {
            return null;
        }
    }

    function renderAvatar(root, user, cacheBust = false) {
        root.dataset.userId = String(user?.id || "");
        root.replaceChildren();
        root.append(make("span", "avatar-fallback", initials(user?.username)));
        const rawUrl = user?.avatar_url || user?.avatarUrl;
        const url = safeAssetUrl(rawUrl);
        if (!url) return;
        const image = make("img");
        image.alt = "";
        if (cacheBust) {
            const parsed = new URL(url);
            parsed.searchParams.set("v", String(Date.now()));
            image.src = parsed.href;
        } else {
            image.src = url;
        }
        image.addEventListener("error", () => image.remove(), { once: true });
        root.append(image);
    }

    function getItems(data, ...keys) {
        if (Array.isArray(data)) return data;
        for (const key of keys) {
            if (Array.isArray(data?.[key])) return data[key];
        }
        return [];
    }

    function getPages(data, fallbackPage = 1) {
        const page = Number(data?.page || data?.current_page || fallbackPage || 1);
        const explicit = Number(data?.pages || data?.page_count || data?.last_page || 0);
        const total = Number(data?.total || data?.total_count || 0);
        const pageSize = Number(data?.page_size || data?.limit || 50);
        return { page, pages: explicit || Math.max(1, Math.ceil(total / pageSize)) };
    }

    function unwrapUser(entry) {
        if (!entry) return {};
        return entry.user ? { ...entry, ...entry.user } : entry;
    }

    function userId(entry) {
        const user = unwrapUser(entry);
        return Number(user.server_id || user.id || user.user_id || 0);
    }

    function userName(entry) {
        const user = unwrapUser(entry);
        return user.username || user.name || `Игрок #${userId(entry)}`;
    }

    function renderUserName(root, entry) {
        const user = unwrapUser(entry);
        const title = user.negative_pp_title || (user.negative_pp_badge ? { tier: 1, name: "Говноед" } : null);
        const tier = Number(title?.tier);
        const ranked = [1, 2, 3].includes(tier);
        root.classList.toggle("has-negative-pp-title", ranked);
        delete root.dataset.negativePpTier;
        root.replaceChildren(make("span", "user-name-text", userName(entry)));
        if (ranked) {
            root.dataset.negativePpTier = String(tier);
            const badge = make("span", "negative-pp-badge", title.name);
            badge.title = `Скоров из расстрельного списка: ${formatNumber(user.negative_pp_score_count || 1)}. Учитываются все режимы, включая результаты вне топ-200.`;
            root.append(badge);
        }
    }

    function userNameNode(entry, tag = "strong") {
        const node = make(tag);
        renderUserName(node, entry);
        return node;
    }

    function openProfile(id) {
        if (!id) return;
        location.hash = `profile/${id}`;
    }

    function beatmapSiteUrl(id, difficultyId = null) {
        const beatmapsetId = Number(id || 0);
        if (!Number.isSafeInteger(beatmapsetId) || beatmapsetId <= 0) return "";
        const beatmapId = Number(difficultyId || 0);
        const exactDifficulty = Number.isSafeInteger(beatmapId) && beatmapId > 0 ? `/${beatmapId}` : "";
        return `/site/#beatmap/${beatmapsetId}${exactDifficulty}`;
    }

    function openBeatmap(id, difficultyId = null) {
        const url = beatmapSiteUrl(id, difficultyId);
        if (url) location.href = url;
    }

    function openGlobalSearch() {
        const dialog = $("#global-search-dialog");
        if (!dialog.open) dialog.showModal();
        window.setTimeout(() => {
            $("#global-search-input").focus();
            $("#global-search-input").select();
        }, 0);
    }

    function closeGlobalSearch() {
        state.search.controller?.abort();
        state.search.controller = null;
        const dialog = $("#global-search-dialog");
        if (dialog.open) dialog.close();
    }

    function renderGlobalPlayers(data) {
        const items = getItems(data, "items", "users");
        const root = $("#global-player-results");
        root.replaceChildren(...items.map((entry) => {
            const user = unwrapUser(entry);
            const row = make("button", "search-result-row global-player-result");
            row.type = "button";
            const avatar = make("span", "avatar");
            renderAvatar(avatar, user);
            const copy = make("span", "search-user-copy");
            copy.append(
                userNameNode(user),
                make("small", "", `${flagEmoji(user.country_code)} ${user.country_code || "XX"} · ${formatNumber(entry.pp || 0)} pp`),
            );
            row.append(avatar, copy, make("b", "", "›"));
            row.addEventListener("click", () => {
                closeGlobalSearch();
                openProfile(userId(user));
            });
            return row;
        }));
        const empty = $("#global-player-empty");
        empty.hidden = items.length > 0;
        if (!items.length) empty.querySelector("span").textContent = "Игроки не найдены";
    }

    async function searchGlobalPlayers() {
        const query = $("#global-search-input").value.trim();
        const empty = $("#global-player-empty");
        $("#global-map-search-link").querySelector("span").textContent = query
            ? `Искать «${query}» в библиотеке карт`
            : "Искать этот запрос в библиотеке карт";
        if (query.length < 2 && !/^#?\d+$/.test(query)) {
            state.search.controller?.abort();
            $("#global-player-results").replaceChildren();
            empty.hidden = false;
            empty.querySelector("span").textContent = "Введите хотя бы 2 символа имени";
            return;
        }
        state.search.controller?.abort();
        const controller = new AbortController();
        state.search.controller = controller;
        const requestId = ++state.search.requestId;
        empty.hidden = false;
        empty.querySelector("span").textContent = "Ищем игроков…";
        try {
            const params = new URLSearchParams({ q: query, mode: state.mode, limit: "10" });
            const data = await api(`/users/search?${params}`, { signal: controller.signal });
            if (requestId === state.search.requestId) renderGlobalPlayers(data);
        } catch (error) {
            if (error.name !== "AbortError" && requestId === state.search.requestId) {
                $("#global-player-results").replaceChildren();
                empty.hidden = false;
                empty.querySelector("span").textContent = error.message;
            }
        } finally {
            if (requestId === state.search.requestId) state.search.controller = null;
        }
    }

    function closeUserMenu(restoreFocus = false) {
        const menu = $("#user-menu-popover");
        const button = $("#user-menu-button");
        if (!menu || !button) return;
        menu.hidden = true;
        button.setAttribute("aria-expanded", "false");
        if (restoreFocus && !button.hidden) button.focus();
    }

    function openUserMenu() {
        if (!state.session?.user) return;
        const menu = $("#user-menu-popover");
        const button = $("#user-menu-button");
        menu.hidden = false;
        button.setAttribute("aria-expanded", "true");
        requestAnimationFrame(() => $("button", menu)?.focus());
    }

    function toggleUserMenu() {
        if ($("#user-menu-popover").hidden) openUserMenu();
        else closeUserMenu();
    }

    function updateAuthUI() {
        const loggedIn = Boolean(state.session?.user);
        $("#profile-avatar-edit").hidden = !loggedIn || Number(state.profile.user?.id) !== Number(state.session.user.id);
        $("#notifications-shell").hidden = !loggedIn;
        if (loggedIn) refreshNotifications().catch(() => {});
        else { notificationItems = []; $("#notifications-items").replaceChildren(); $("#notifications-count").hidden = true; closeNotifications(); }
        $("#auth-button").hidden = loggedIn;
        $(".user-menu-shell").hidden = !loggedIn;
        $("#user-menu-button").hidden = !loggedIn;
        if (loggedIn) {
            const user = state.session.user;
            renderUserName($("#header-name"), user);
            renderAvatar($("#header-avatar"), user);
            renderAvatar($("#user-menu-avatar"), user);
            renderUserName($("#user-menu-name"), user);
            $("#user-menu-detail").textContent = `${flagEmoji(user.country_code)} ${user.country_code || "XX"} · ID #${user.server_id || user.id}`;
            $("#user-menu-button").setAttribute("aria-label", `Открыть меню пользователя ${user.username}`);
        } else closeUserMenu();
        $("#user-menu-admin").hidden = !hasSitePermission("admin_panel");
        if (state.route === "settings") renderSettingsGate();
    }

    function parseRoute() {
        const clean = location.hash.replace(/^#/, "");
        const [rawRoute, rawId, rawDifficultyId, rawMode] = clean.split("/");
        const route = ["home", "rankings", "beatmaps", "beatmap", "profile", "settings"].includes(rawRoute) ? rawRoute : "home";
        return {
            route,
            id: /^\d+$/.test(rawId || "") ? Number(rawId) : null,
            difficultyId: /^\d+$/.test(rawDifficultyId || "") ? Number(rawDifficultyId) : null,
            beatmapMode: ["osu", "taiko", "fruits", "mania"].includes(rawMode) ? rawMode : null,
        };
    }

    async function route() {
        const parsed = parseRoute();
        const previousRoute = state.route;
        const previousProfileId = state.profile.id;
        state.route = parsed.route;
        if (state.route !== "beatmap") {
            $("#beatmap-preview-audio").pause();
            ++state.beatmapDetail.detailRequestId;
            ++state.beatmapDetail.scoreRequestId;
            ++state.beatmapDetail.commentRequestId;
        }
        document.body.dataset.route = parsed.route;
        const routeTitles = { home: "главная", beatmaps: "библиотека карт", beatmap: "информация о карте", rankings: "рейтинг", profile: "информация об игроке", settings: "настройки" };
        $("#mobile-page-title").textContent = routeTitles[parsed.route];
        $(".site-header").classList.remove("is-menu-open");
        $("#mobile-menu-button").setAttribute("aria-expanded", "false");
        closeUserMenu();
        if ($("#global-search-dialog").open) closeGlobalSearch();
        if (parsed.route === "profile") {
            state.profile.id = parsed.id || state.session?.user?.id || null;
            if (previousRoute !== "profile" || Number(previousProfileId) !== Number(state.profile.id)) {
                state.profile.page = 1;
                state.profile.scoreLimit = 5;
            }
        } else if (previousRoute === "profile") {
            state.profile.page = 1;
            state.profile.scoreLimit = 5;
            closeScoreActionMenu();
        }
        if (parsed.route === "beatmap") {
            if (state.beatmapDetail.id !== parsed.id) {
                state.beatmapDetail.scope = "global";
                state.beatmapDetail.mods = null;
                $("#beatmap-comment-text").value = "";
            }
            state.beatmapDetail.id = parsed.id;
            state.beatmapDetail.beatmapId = parsed.difficultyId;
            state.beatmapDetail.mode = parsed.beatmapMode;
        }
        $$("[data-page]").forEach((page) => {
            const active = page.dataset.page === parsed.route;
            page.hidden = !active;
            page.classList.toggle("is-active", active);
        });
        $$("[data-route]").forEach((link) => {
            const active = link.dataset.route === parsed.route || (parsed.route === "beatmap" && link.dataset.route === "beatmaps");
            link.classList.toggle("is-active", active);
        });
        window.scrollTo({ top: 0, behavior: "auto" });
        try {
            await busy(loadRoute(parsed.route));
        } catch (error) {
            if (error.name !== "AbortError") toast(error.message || "Не удалось загрузить страницу", "error");
        }
    }

    function loadRoute(name) {
        const loaders = {
            home: loadHome,
            rankings: loadRankings,
            beatmaps: loadBeatmaps,
            beatmap: loadBeatmapDetail,
            profile: loadProfile,
            settings: loadSettings,
        };
        return loaders[name]();
    }

    function go(name) {
        if (location.hash === `#${name}`) route();
        else location.hash = name;
    }

    function updateBeatmapPagination(loading = false) {
        const beatmaps = state.beatmaps;
        const hasPrevious = beatmaps.page > 1;
        const hasNext = typeof beatmaps.nextCursor === "string" && beatmaps.nextCursor.length > 0;
        $("#beatmaps-pagination").hidden = !hasPrevious && !hasNext;
        $("#beatmaps-page").textContent = `Страница ${beatmaps.page}`;
        $("#beatmaps-prev").disabled = loading || !hasPrevious;
        $("#beatmaps-next").disabled = loading || !hasNext;
    }

    function resetBeatmapPagination() {
        const beatmaps = state.beatmaps;
        beatmaps.controller?.abort();
        beatmaps.controller = null;
        beatmaps.requestId += 1;
        beatmaps.page = 1;
        beatmaps.cursorHistory = [null];
        beatmaps.nextCursor = null;
        updateBeatmapPagination(false);
    }

    function setMode(mode) {
        if (!modes.some((item) => item.value === mode)) return;
        state.mode = mode;
        state.rankings.page = 1;
        resetBeatmapPagination();
        state.profile.page = 1;
        state.profile.scoreLimit = 5;
        $("#global-mode").value = mode;
        renderModeTabs();
        if (state.route !== "settings") {
            busy(loadRoute(state.route)).catch((error) => toast(error.message, "error"));
        }
    }

    function renderModeTabs() {
        $$('[data-mode-tabs]').forEach((root) => {
            root.replaceChildren();
            const compact = !root.classList.contains("mode-tabs-filter");
            modes.forEach((mode) => {
                const button = make("button", `mode-tab${mode.value === state.mode ? " is-active" : ""}`, compact ? "" : mode.label);
                button.type = "button";
                button.dataset.mode = mode.value;
                if (compact) button.append(rulesetIcon(mode.value));
                button.title = mode.label;
                button.setAttribute("aria-label", mode.label);
                button.setAttribute("aria-pressed", String(mode.value === state.mode));
                button.addEventListener("click", () => setMode(mode.value));
                root.append(button);
            });
        });
    }

    function statValue(stats, ...names) {
        for (const name of names) {
            if (stats?.[name] !== undefined && stats?.[name] !== null) return stats[name];
        }
        return 0;
    }

    async function loadHome() {
        const [data, mapData] = await Promise.all([
            api(`/home?mode=${encodeURIComponent(state.mode)}`),
            api(`/beatmapsets?${new URLSearchParams({ q: "", mode: state.mode, status: "leaderboard", sort: "ranked_desc" })}`).catch(() => ({ items: [] })),
        ]);
        state.home = data;
        const stats = data.counts || data.stats || data.statistics || data;
        $("#hero-online").textContent = formatCompact(statValue(stats, "online", "online_users", "users_online"));
        $("#hero-users").textContent = formatCompact(statValue(stats, "users", "total_users", "users_total"));
        $("#hero-scores").textContent = formatCompact(statValue(stats, "scores", "total_scores", "scores_total"));

        renderOnlineHistory(data.online_history);
        const scores = getItems(data, "recent_scores", "scores", "activity").slice(0, 5);
        renderHomeScores(scores);
        const mapItems = getItems(mapData, "items", "beatmapsets", "maps");
        const beatmaps = mapItems.slice(0, 4);
        renderMapCards($("#home-beatmaps"), beatmaps);
        $("#home-beatmaps-empty").hidden = beatmaps.length > 0;
        const releases = data.local_releases || [];
        renderMapCards($("#home-local-releases"), releases);
        Array.from($("#home-local-releases").children).forEach((card, index) => {
            const release = releases[index];
            const kind = release.status === "loved" ? "Loved" : "Ranked";
            const label = make("strong", `local-release-kind is-${release.status}`, kind);
            const date = make("time", "local-release-date", formatShortDate(release.status_changed_at));
            date.title = `Получила ${kind} на SOMS! ${formatShortDate(release.status_changed_at)}`;
            card.querySelector(".map-meta").replaceChildren(label, date);
        });
        $("#home-local-releases-empty").hidden = releases.length > 0;
        const onlineUsers = getItems(data, "online_users", "online");
        const activeUser = onlineUsers[0] || scores[0] || { username: "кто-то из друзей" };
        renderUserName($("#hero-player-name"), activeUser);
    }

    function renderOnlineHistory(history) {
        const chart = $("#home-online-chart");
        const path = chart.querySelector("path");
        const dot = chart.querySelector("circle");
        const points = (history?.points || []).filter(p => Number.isFinite(p.time) && Number.isFinite(p.users) && p.users >= 0);
        const maximum = Math.max(1, ...points.map(p => p.users));
        const start = history?.start || 0;
        const duration = Math.max(1, (history?.end || 0) - start);
        let previous;
        path.setAttribute("d", points.map(p => {
            const x = 3 + 334 * Math.max(0, Math.min(1, (p.time - start) / duration));
            const y = 66 - 60 * p.users / maximum;
            const command = !previous || p.time - previous.time > (history.interval || 600) * 1.5 ? "M" : "L";
            previous = p;
            return `${command}${x.toFixed(2)} ${y.toFixed(2)}`;
        }).join(" "));
        dot.toggleAttribute("hidden", !points.length);
        if (points.length) {
            const last = points[points.length - 1];
            dot.setAttribute("cx", String(3 + 334 * (last.time - start) / duration));
            dot.setAttribute("cy", String(66 - 60 * last.users / maximum));
        }
        const caption = points.length ? `Онлайн за 24 часа · пик ${formatNumber(Math.max(...points.map(p => p.users)))}` : "Онлайн за 24 часа · история накапливается";
        $("#home-online-chart-caption").textContent = caption;
        chart.setAttribute("aria-label", caption);
        chart.onpointermove = event => {
            if (!points.length) return;
            const rect = chart.getBoundingClientRect();
            const time = start + (event.clientX - rect.left) / rect.width * duration;
            const point = points.reduce((a, b) => Math.abs(b.time - time) < Math.abs(a.time - time) ? b : a);
            chart.title = Math.abs(point.time - time) > (history.interval || 600) ? "Нет данных за этот период" : `${new Date(point.time * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })} · ${formatNumber(point.users)} в сети`;
        };
    }

    function scoreBeatmap(score) {
        const beatmap = score.beatmap || {};
        const beatmapset = score.beatmapset || beatmap.beatmapset || {};
        return {
            id: Number(beatmap.id || score.beatmap_id || 0),
            beatmapsetId: Number(beatmapset.id || beatmap.beatmapset_id || score.beatmapset_id || 0),
            title: beatmapset.title || score.beatmap_title || "Неизвестная карта",
            artist: beatmapset.artist || score.beatmap_artist || "Неизвестный артист",
            version: beatmap.version || score.beatmap_version || "",
            creator: beatmapset.creator || "",
            cover: beatmapset.covers?.list || beatmapset.covers?.card || beatmapset.cover_url || score.beatmap_cover || null,
        };
    }

    function scoreGrade(score) {
        const grade = String(score.rank || score.grade || score.rank_letter || "—").toUpperCase();
        if (grade === "XH") return "SSH";
        if (grade === "X") return "SS";
        return grade;
    }

    function gradeClass(grade) {
        const normalized = String(grade || "").toUpperCase();
        return /^[A-Z]{1,3}$/.test(normalized) ? `is-${normalized.toLowerCase()}` : "";
    }

    function scoreMods(score) {
        const mods = score.mods || [];
        if (!Array.isArray(mods) || !mods.length) return "NM";
        return mods.map((mod) => typeof mod === "string" ? mod : mod.acronym).filter(Boolean).join("") || "NM";
    }

    function renderHomeScores(items) {
        const root = $("#home-scores");
        root.replaceChildren();
        $("#home-scores-empty").hidden = items.length > 0;
        items.forEach((score) => {
            const map = scoreBeatmap(score);
            const row = make("button", `activity-item${score.negative_pp ? " is-negative-pp" : ""}`);
            row.type = "button";
            row.setAttribute("aria-label", `Подробнее о результате: ${map.artist} — ${map.title}`);
            row.addEventListener("click", () => showScoreDetails(score));
            const grade = scoreGrade(score);
            row.append(make("span", `activity-grade ${gradeClass(grade)}`.trim(), grade));
            const copy = make("span", "activity-copy");
            const provenance = score.import
                ? ` · импорт: ${score.import.source === "official_osu" ? "official osu!" : score.import.source || "другой сервер"}`
                : "";
            const playerLine = make("span");
            playerLine.append(userNameNode(score, "span"), document.createTextNode(` · ${map.version || scoreMods(score)}${provenance}`));
            copy.append(make("strong", "", `${map.artist} — ${map.title}`), playerLine);
            const result = make("span", "activity-result");
            result.append(createScorePp(score, true), make("small", "", formatDate(score.ended_at || score.created_at)));
            row.append(copy, result);
            root.append(row);
        });
    }

    function statusName(status, local = false) {
        const numeric = Number(status);
        const names = { "-2": "Заброшенная", "-1": "В разработке", 0: "На рассмотрении", 1: "Рейтинговая", 2: "Одобренная", 3: "Квалифицированная", 4: "Любимая" };
        const aliases = {
            graveyard: "Заброшенная", wip: "В разработке", pending: "На рассмотрении",
            ranked: "Рейтинговая", approved: "Одобренная", qualified: "Квалифицированная", loved: "Любимая",
        };
        const label = Number.isFinite(numeric) && names[numeric] !== undefined
            ? names[numeric]
            : aliases[String(status || "pending").toLowerCase()] || String(status || "На рассмотрении");
        return `${local ? "SOMS! " : ""}${label}`;
    }

    function statusKey(status) {
        const numeric = Number(status);
        const keys = { "-2": "graveyard", "-1": "wip", 0: "pending", 1: "ranked", 2: "approved", 3: "qualified", 4: "loved" };
        return Number.isFinite(numeric) && keys[numeric] ? keys[numeric] : String(status || "pending").toLowerCase();
    }

    function mapData(entry) {
        const effective = entry.local_policy || entry.effective || entry.ranking || {};
        const beatmaps = Array.isArray(entry.beatmaps) ? entry.beatmaps : [];
        return {
            id: Number(entry.id || entry.beatmapset_id || 0),
            title: entry.title || "Без названия",
            artist: entry.artist || "Неизвестный артист",
            creator: entry.creator || entry.mapper || "неизвестен",
            status: effective.status ?? entry.status ?? entry.beatmap_status ?? 0,
            local: Boolean(entry.is_locally_ranked || entry.local_ranked || ["local_set", "local_difficulty", "beatmapset", "beatmap"].includes(effective.source)),
            leaderboard: effective.leaderboard_enabled,
            pp: effective.pp_enabled,
            plays: entry.play_count ?? entry.plays ?? 0,
            difficulties: beatmaps.length || entry.difficulty_count || 0,
            stars: beatmaps.map((beatmap) => Number(beatmap.difficulty_rating || beatmap.stars || 0)).filter((value) => value > 0),
            bpm: Number(entry.bpm || 0),
            favourites: Number(entry.favourite_count ?? entry.favourites ?? 0),
            cover: entry.covers?.["cover@2x"] || entry.covers?.cover || entry.covers?.["card@2x"] || entry.covers?.card || entry.cover_url || entry.cover,
            statusChangedAt: entry.status_changed_at || effective.status_changed_at || entry.ranked_date || null,
            updatedAt: entry.last_updated || null,
            submittedAt: entry.submitted_date || null,
            source: entry.source || null,
            description: entry.description || null,
            tags: entry.tags || "",
            beatmaps,
        };
    }

    function difficultyClass(stars) {
        if (stars < 1.5) return "is-beginner";
        if (stars < 2.25) return "is-easy";
        if (stars < 3) return "is-normal";
        if (stars < 4) return "is-hard";
        if (stars < 5) return "is-insane";
        if (stars < 6) return "is-expert";
        if (stars < 7) return "is-expert-plus";
        if (stars < 8) return "is-master";
        return "is-master-plus";
    }

    function mapStatusSymbol(status, local = false) {
        if (local) return "◆";
        return {
            ranked: "★",
            approved: "✓",
            qualified: "◇",
            loved: "♥",
            pending: "…",
            wip: "…",
            graveyard: "×",
        }[statusKey(status)] || "•";
    }

    function createMapCard(entry) {
        const map = mapData(entry);
        const card = make("article", "map-card");
        state.beatmapCache.set(map.id, entry);
        const statusLabel = map.local
            ? `${statusName(map.status, false)} на SOMS!`
            : statusName(map.status, false);
        const cover = make("div", "map-cover");
        const coverUrl = safeAssetUrl(map.cover);
        if (coverUrl) {
            const image = make("img", "map-cover-image");
            image.src = coverUrl;
            image.alt = "";
            cover.prepend(image);
            cover.classList.add("has-image");
        }
        const status = make("span", `map-status map-card-status${map.local ? " is-local" : ""}`, mapStatusSymbol(map.status, map.local));
        status.dataset.status = statusKey(map.status);
        status.title = statusLabel;
        status.setAttribute("role", "img");
        status.setAttribute("aria-label", statusLabel);
        cover.append(status);
        const body = make("div", "map-body");
        const primaryLink = make("a", "map-card-link");
        primaryLink.href = beatmapSiteUrl(map.id);
        primaryLink.draggable = false;
        primaryLink.title = `${statusLabel}: открыть ${map.artist} — ${map.title}`;
        primaryLink.setAttribute("aria-label", `Открыть карту ${map.artist} — ${map.title}. ${statusLabel}`);
        body.append(
            make("h3", "", map.title),
            make("p", "map-artist", map.artist),
            make("p", "map-creator", `автор ${map.creator}`),
        );
        if (map.stars.length) {
            const difficulties = make("div", "map-difficulties");
            map.stars.slice().sort((a, b) => a - b).forEach((stars) => {
                const dot = make("i", `difficulty-dot ${difficultyClass(stars)}`);
                dot.title = `${stars.toFixed(2)}★`;
                difficulties.append(dot);
            });
            body.append(difficulties);
        }
        const meta = make("div", "map-meta");
        const facts = make("span");
        const summary = [
            map.bpm ? `${formatNumber(map.bpm)} BPM` : null,
            `${formatCompact(map.plays)} игр`,
            map.favourites ? `♥ ${formatCompact(map.favourites)}` : null,
        ].filter(Boolean).join(" · ");
        facts.append(make("strong", "", summary));
        if (map.local) {
            facts.title = `${map.leaderboard ? "Есть лидерборд" : "Без лидерборда"}; ${map.pp ? "начисляется PP" : "без PP"}`;
        }
        const mapDate = map.statusChangedAt || map.updatedAt;
        if (mapDate) {
            const dateMeaning = map.statusChangedAt ? "статус с" : "обновлена";
            const date = make("time", "map-status-date", "◷");
            const description = `${dateMeaning} ${formatShortDate(mapDate)}`;
            const parsedMapDate = new Date(mapDate);
            if (!Number.isNaN(parsedMapDate.getTime())) date.dateTime = parsedMapDate.toISOString();
            date.title = description;
            date.setAttribute("aria-label", description);
            facts.append(document.createTextNode(" "), date);
        }
        const download = make("a", "map-download", "Скачать ↓");
        download.href = `${API_ROOT}/beatmapsets/${map.id}/download`;
        download.download = "";
        download.target = "_blank";
        download.rel = "noopener";
        download.setAttribute("aria-label", `Скачать ${map.artist} — ${map.title}`);
        download.addEventListener("click", (event) => {
            if (!state.session) {
                event.preventDefault();
                toast("Войди, чтобы скачивать карты", "error");
                openAuth("login");
            }
        });
        meta.append(facts, download);
        body.append(meta);
        card.append(primaryLink, cover, body);
        return card;
    }

    function renderMapCards(root, items) {
        const validItems = items.filter((item) => mapData(item).id);
        root.replaceChildren(...validItems.map(createMapCard));
        return validItems.length;
    }

    function detailDescription(value) {
        const source = typeof value === "string"
            ? value
            : value && typeof value === "object"
                ? value.description || value.bbcode || value.raw || ""
                : "";
        if (!String(source).trim()) return "";

        // Upstream descriptions contain rendered BBCode HTML. Parse it in an inert
        // document and copy text only, adding separators for visual block elements.
        const parsed = new DOMParser().parseFromString(String(source), "text/html");
        const blocks = new Set([
            "ADDRESS", "ARTICLE", "ASIDE", "BLOCKQUOTE", "DD", "DIV", "DL", "DT", "FIGCAPTION",
            "FIGURE", "FOOTER", "H1", "H2", "H3", "H4", "H5", "H6", "HEADER", "HR", "LI",
            "MAIN", "NAV", "OL", "P", "PRE", "SECTION", "TABLE", "TBODY", "TFOOT", "THEAD", "TR", "UL",
        ]);
        const ignored = new Set(["EMBED", "IFRAME", "MATH", "NOSCRIPT", "OBJECT", "SCRIPT", "STYLE", "SVG", "TEMPLATE"]);
        const chunks = [];
        const walk = (node) => {
            if (node.nodeType === 3) {
                chunks.push(node.nodeValue || "");
                return;
            }
            if (node.nodeType !== 1 || ignored.has(node.tagName)) return;
            if (node.tagName === "BR") {
                chunks.push("\n");
                return;
            }
            const block = blocks.has(node.tagName);
            if (block) chunks.push("\n");
            if (node.tagName === "LI") chunks.push("• ");
            node.childNodes.forEach(walk);
            if (block) chunks.push("\n");
        };
        parsed.body.childNodes.forEach(walk);
        return chunks.join("")
            .replace(/\u00a0/g, " ")
            .replace(/\r\n?/g, "\n")
            .replace(/[\t\f\v ]+/g, " ")
            .replace(/ *\n */g, "\n")
            .replace(/\n{3,}/g, "\n\n")
            .trim();
    }

    function detailModeLabel(value) {
        const numericModes = { 0: "osu!", 1: "taiko", 2: "catch", 3: "mania" };
        if (numericModes[value] !== undefined) return numericModes[value];
        return modes.find((mode) => mode.value === String(value))?.label || String(value || "osu!");
    }

    function renderBeatmapModerationActions(map) {
        const root = $("#beatmap-moderation-actions");
        const allowed = hasSitePermission("beatmap_moderation");
        root.hidden = !allowed;
        if (!allowed) return;

        const key = statusKey(map.status);
        const ranked = ["ranked", "approved"].includes(key) && map.pp !== false;
        const loved = key === "loved" && map.leaderboard !== false;
        const hasLeaderboard = map.leaderboard === true || ["ranked", "approved", "qualified", "loved"].includes(key);
        $("#beatmap-rank").hidden = ranked;
        $("#beatmap-unrank").hidden = !hasLeaderboard;
        $("#beatmap-love").hidden = loved;
    }

    async function moderateBeatmapset(action, button) {
        if (!hasSitePermission("beatmap_moderation")) return;
        const beatmapsetId = Number(state.beatmapDetail.id || 0);
        if (!beatmapsetId) return;
        const busyLabels = { rank: "Ранкуем…", unrank: "Деранкаем…", love: "Добавляем в Loved…" };
        const successLabels = {
            rank: "Карта получила Ranked на SOMS!",
            unrank: "Локальный статус снят или карта перенесена в заброшенные",
            love: "Карта получила Loved: лидерборд включён, PP отключены",
        };
        $$("button", $("#beatmap-moderation-actions")).forEach((item) => { item.disabled = true; });
        setButtonBusy(button, true, busyLabels[action]);
        try {
            await api(`/beatmapsets/${beatmapsetId}/moderation`, { method: "POST", data: { action } });
            toast(successLabels[action]);
            await loadBeatmapDetail();
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
            $$("button", $("#beatmap-moderation-actions")).forEach((item) => { item.disabled = false; });
        }
    }

    function beatmapMode(value) {
        return ({ 0: "osu", 1: "taiko", 2: "fruits", 3: "mania", catch: "fruits" })[value] || value || "osu";
    }

    function beatmapModeSymbol(mode) {
        return { osu: "◉", taiko: "◍", fruits: "⠿", mania: "▥" }[beatmapMode(mode)] || "◉";
    }

    function rulesetIcon(mode) {
        const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.setAttribute("viewBox", "0 0 24 24");
        svg.setAttribute("aria-hidden", "true");
        const paths = {
            osu: "M12 5a7 7 0 1 0 0 14a7 7 0 1 0 0-14",
            taiko: "M12 5a7 7 0 1 0 0 14a7 7 0 1 0 0-14M12 1v22",
            fruits: "M9 8v1m7 2v1m-7 3v1",
            mania: "M8 8v8m4-10v12m4-10v8",
        };
        const circle = document.createElementNS(svg.namespaceURI, "circle");
        circle.setAttribute("cx", "12"); circle.setAttribute("cy", "12"); circle.setAttribute("r", "11");
        svg.append(circle);
        for (const d of (mode === "osurx" || mode === "osuap" ? [] : [paths[mode] || paths.osu])) {
            const path = document.createElementNS(svg.namespaceURI, "path");
            path.setAttribute("d", d);
            svg.append(path);
        }
        const icon = make("span", "ruleset-icon");
        icon.append(svg);
        if (mode === "osurx" || mode === "osuap") {
            const text = document.createElementNS(svg.namespaceURI, "text");
            text.setAttribute("x", "12"); text.setAttribute("y", "12");
            text.textContent = mode === "osurx" ? "RX" : "AP";
            svg.append(text);
        }
        return icon;
    }

    function beatmapSupportsMode(beatmap, mode) {
        const nativeMode = beatmapMode(beatmap.mode);
        return nativeMode === mode || nativeMode === "osu";
    }

    async function selectBeatmapMode(mode) {
        const detail = state.beatmapDetail;
        const difficulties = mapData(detail.data || {}).beatmaps.filter((beatmap) => Number(beatmap.id) > 0 && beatmapSupportsMode(beatmap, mode));
        const selected = difficulties.find((beatmap) => Number(beatmap.id) === Number(detail.beatmapId))
            || difficulties.find((beatmap) => beatmapMode(beatmap.mode) === mode) || difficulties[0];
        if (selected) await selectBeatmapDifficulty(selected, mode);
    }

    function hasBeatmapSupporter() {
        const user = state.session?.user;
        return Boolean(user?.is_supporter || user?.roles?.includes("supporter"));
    }

    function renderBeatmapDetail(entry, notice = "") {
        const map = mapData(entry);
        state.beatmapDetail.data = entry;
        state.beatmapCache.set(map.id, entry);
        $("#beatmap-detail-content").hidden = false;
        $("#beatmap-detail-empty").hidden = true;
        const cover = $("#beatmap-detail-cover");
        $$(':scope > img', cover).forEach((image) => image.remove());
        const coverUrl = safeAssetUrl(map.cover);
        if (coverUrl) {
            const image = make("img", "beatmap-detail-cover-image");
            image.src = coverUrl;
            image.alt = "";
            image.addEventListener("error", () => image.remove(), { once: true });
            cover.prepend(image);
        }
        $("#beatmap-detail-title").textContent = map.title;
        $("#beatmap-detail-artist").textContent = map.artist;
        $("#beatmap-detail-creator").textContent = map.creator;
        $("#beatmap-detail-submitted").textContent = `Загружена ${formatShortDate(map.submittedAt)}`;
        $("#beatmap-detail-ranked").textContent = map.statusChangedAt
            ? `Дата статуса: ${formatShortDate(map.statusChangedAt)}`
            : `Обновлена ${formatShortDate(map.updatedAt)}`;
        const mapperId = Number(entry.user_id || 0);
        $("#beatmap-mapper-link").href = mapperId ? `https://osu.ppy.sh/users/${mapperId}` : `https://osu.ppy.sh/beatmapsets/${map.id}`;
        $("#beatmap-mapper-link").title = `Маппер ${map.creator} на osu!`;
        $("#beatmap-detail-creator").href = $("#beatmap-mapper-link").href;
        $("#beatmap-detail-creator").title = $("#beatmap-mapper-link").title;
        const avatar = $("#beatmap-mapper-avatar");
        avatar.onerror = () => { avatar.onerror = null; avatar.src = "/site/soms-default-avatar.png"; };
        avatar.src = mapperId ? `https://a.ppy.sh/${mapperId}` : "/site/soms-default-avatar.png";
        $("#beatmap-detail-plays").textContent = `▶ ${formatNumber(map.plays)} игр на osu!`;
        $("#beatmap-detail-favourites").textContent = `♥ ${formatNumber(map.favourites)}`;
        $("#beatmap-detail-favourites").title = "В избранном на osu!";
        $("#beatmap-detail-notice").textContent = notice;
        $("#beatmap-detail-notice").hidden = !notice;
        const difficulties = map.beatmaps.filter((beatmap) => Number(beatmap?.id) > 0).slice()
            .sort((a, b) => Number(a.difficulty_rating || 0) - Number(b.difficulty_rating || 0));
        $("#beatmap-detail-difficulties").replaceChildren(...difficulties.map((beatmap) => {
            const stars = Number(beatmap.difficulty_rating || 0);
            const row = make("button", "difficulty-row");
            row.type = "button";
            row.dataset.beatmapId = String(beatmap.id);
            row.dataset.mode = beatmapMode(beatmap.mode);
            const label = `${detailModeLabel(beatmap.mode)} · ${beatmap.version} · ${numberWithComma(stars)}★`;
            row.title = label;
            row.setAttribute("aria-label", label);
            row.append(make("span", `difficulty-gem ${difficultyClass(stars)}`, beatmapModeSymbol(beatmap.mode)));
            row.addEventListener("click", () => busy(selectBeatmapDifficulty(beatmap)));
            return row;
        }));
        const modes = ["osu", "taiko", "fruits", "mania", "osurx", "osuap"];
        $("#beatmap-detail-modes").replaceChildren(...modes.map((mode) => {
            const count = difficulties.filter((beatmap) => beatmapMode(beatmap.mode) === ((mode === "osurx" || mode === "osuap") ? "osu" : mode)).length;
            const button = make("button");
            button.type = "button";
            button.dataset.mode = mode;
            button.dataset.count = String(count);
            button.disabled = !difficulties.some((beatmap) => beatmapSupportsMode(beatmap, mode));
            button.title = `${detailModeLabel(mode)} · Авторских сложностей: ${count}${button.disabled ? " · Нет совместимых карт" : ""}`;
            button.setAttribute("aria-label", button.title);
            button.append(rulesetIcon(mode));
            if (count) button.append(make("span", "beatmap-mode-count", formatNumber(count)));
            button.addEventListener("click", () => busy(selectBeatmapMode(mode)));
            return button;
        }));
        const description = detailDescription(map.description);
        $("#beatmap-detail-description").textContent = description || "У этой карты пока нет описания.";
        const tags = Array.isArray(map.tags) ? map.tags : String(map.tags || "").split(/\s+/);
        $("#beatmap-detail-tags").replaceChildren(...tags.filter(Boolean).map((tag) => make("span", "", tag)));
        const genres = ["Любой", "Не указан", "Видеоигры", "Аниме", "Рок", "Поп", "Другое", "Юмор", "", "Хип-хоп", "Электроника", "Метал", "Классика", "Фолк", "Джаз"];
        const languages = ["Любой", "Не указан", "Английский", "Японский", "Китайский", "Инструментальная", "Корейский", "Французский", "Немецкий", "Шведский", "Испанский", "Итальянский", "Русский", "Польский", "Другой"];
        const facts = [["Жанр", genres[entry.genre?.id] || "Не указан"], ["Язык", languages[entry.language?.id] || "Не указан"]];
        if (map.source) facts.push(["Источник", map.source]);
        const metadata = $("#beatmap-detail-metadata");
        metadata.replaceChildren(...facts.flatMap(([key, value]) => [make("dt", "", key), make("dd", "", value)]));
        const rawRatings = Array.isArray(entry.ratings) ? entry.ratings.map(Number) : [];
        const ratings = rawRatings.length === 11 ? rawRatings.slice(1) : rawRatings;
        const votes = ratings.reduce((sum, value) => sum + value, 0);
        const rating = $("#beatmap-user-rating");
        rating.hidden = !votes;
        rating.replaceChildren();
        if (votes) {
            const average = ratings.reduce((sum, value, index) => sum + value * (index + 1), 0) / votes;
            rating.append(make("strong", "", `Оценка на osu! · ${numberWithComma(average)} / 10`));
            const bars = make("div", "beatmap-rating-bars");
            ratings.forEach((value, index) => {
                const bar = make("span");
                bar.style.height = `${value / Math.max(...ratings) * 100}%`;
                bar.title = `${index + 1}: ${formatNumber(value)} оценок`;
                bars.append(bar);
            });
            rating.append(bars);
        }
        const download = $("#beatmap-detail-download");
        download.href = `${API_ROOT}/beatmapsets/${map.id}/download`;
        download.setAttribute("download", "");
        download.onclick = (event) => {
            if (state.session) return;
            event.preventDefault();
            toast("Войдите, чтобы скачать карту", "error");
            openAuth("login");
        };
        $("#beatmap-detail-mirror").href = `https://beatconnect.io/b/${map.id}`;
        $("#beatmap-detail-official").href = `https://osu.ppy.sh/beatmapsets/${map.id}`;
        const audio = $("#beatmap-preview-audio");
        audio.pause();
        audio.src = `/api/private/audio/beatmapset/${map.id}`;
        $("#beatmap-preview-toggle").disabled = false;
        renderBeatmapModerationActions(map);
        const selected = difficulties.find((beatmap) => Number(beatmap.id) === Number(state.beatmapDetail.beatmapId))
            || difficulties.find((beatmap) => beatmap.leaderboard_enabled !== false) || difficulties[0];
        $("#beatmap-leaderboard").hidden = !selected;
        if (selected) busy(selectBeatmapDifficulty(selected));
        state.beatmapDetail.commentPage = 1;
        loadBeatmapComments();
    }

    function renderSelectedBeatmap(beatmap) {
        const map = mapData(state.beatmapDetail.data || {});
        const mode = state.beatmapDetail.mode;
        const converted = mode !== beatmapMode(beatmap.mode);
        const value = (number) => number == null ? "—" : numberWithComma(number);
        $("#beatmap-selected-stars").textContent = `★ ${value(beatmap.difficulty_rating)}`;
        $("#beatmap-selected-name").textContent = beatmap.version || "Без названия";
        const owners = beatmap.owners?.length ? `Сложность от ${beatmap.owners.map((owner) => owner.username).join(", ")}` : "";
        $("#beatmap-selected-owners").textContent = [owners, converted ? "Конверт из osu! · параметры исходной карты" : ""].filter(Boolean).join(" · ");
        const status = $("#beatmap-detail-status");
        const effective = beatmap.status ?? map.status;
        status.textContent = statusName(effective, map.local);
        status.dataset.status = statusKey(effective);
        const metrics = [
            ["Длина", beatmap.total_length == null ? "—" : formatMapLength(beatmap.total_length)],
            ["BPM", value(beatmap.bpm ?? map.bpm)],
            [beatmapMode(beatmap.mode) === "mania" ? "Ноты" : "Круги", value(beatmap.count_circles)],
            [beatmapMode(beatmap.mode) === "mania" ? "Длинные" : "Слайдеры", value(beatmap.count_sliders)],
        ];
        $("#beatmap-detail-metrics").replaceChildren(...metrics.map(([label, text]) => {
            const root = make("div");
            root.append(make("strong", "", text), make("small", "", label));
            return root;
        }));
        const bars = [
            [beatmapMode(beatmap.mode) === "mania" ? "Клавиши" : "Размер нот", beatmap.cs],
            ["Потеря HP", beatmap.drain], ["Точность", beatmap.accuracy],
            ["Скорость появления", beatmap.ar], ["Сложность", beatmap.difficulty_rating],
        ];
        $("#beatmap-detail-facts").replaceChildren(...bars.map(([label, number], index) => {
            const root = make("div", index === 4 ? "is-stars" : "");
            const bar = make("meter");
            bar.min = 0;
            bar.max = Math.max(10, Number(number || 0));
            bar.value = Number(number || 0);
            bar.setAttribute("aria-label", label);
            const dd = make("dd");
            dd.append(bar, make("b", "", value(number)));
            root.append(make("dt", "", label), dd);
            return root;
        }));
        $$("#beatmap-detail-modes button").forEach((button) => button.setAttribute("aria-pressed", String(button.dataset.mode === mode)));
        $$("#beatmap-detail-difficulties button").forEach((button) => {
            const active = Number(button.dataset.beatmapId) === Number(beatmap.id);
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-pressed", String(active));
            button.hidden = !beatmapSupportsMode({ mode: button.dataset.mode }, mode);
        });
        $("#beatmap-pass-rate").textContent = "Загружаем статистику…";
        api(`/beatmaps/${beatmap.id}/activity?mode=${mode}`).then((activity) => {
            if (Number(state.beatmapDetail.beatmapId) !== Number(beatmap.id) || state.beatmapDetail.mode !== mode) return;
            const root = $("#beatmap-pass-rate");
            root.replaceChildren();
            if (!activity.plays) { root.textContent = "Пока нет сохранённых прохождений"; return; }
            const meter = make("meter");
            meter.min = 0; meter.max = activity.plays; meter.value = activity.passes;
            meter.setAttribute("aria-label", "Доля успешных прохождений");
            root.append(meter, make("strong", "", `${numberWithComma(activity.passes / activity.plays * 100, 1)}%`),
                make("p", "", `${formatNumber(activity.passes)} из ${formatNumber(activity.plays)} игр`));
        }).catch(() => {
            if (Number(state.beatmapDetail.beatmapId) === Number(beatmap.id) && state.beatmapDetail.mode === mode) $("#beatmap-pass-rate").textContent = "Статистика временно недоступна";
        });
        renderBeatmapFilters();
    }

    async function loadBeatmapDetail() {
        const id = Number(state.beatmapDetail.id || 0);
        const requestId = ++state.beatmapDetail.detailRequestId;
        ++state.beatmapDetail.scoreRequestId;
        ++state.beatmapDetail.commentRequestId;
        $("#beatmap-preview-audio").pause();
        const loading = $("#beatmap-detail-loading");
        const empty = $("#beatmap-detail-empty");
        $("#beatmap-detail-content").hidden = true;
        empty.hidden = true;
        loading.hidden = false;
        if (!id) {
            loading.hidden = true;
            empty.hidden = false;
            return;
        }

        const cached = state.beatmapCache.get(id);
        try {
            const entry = await api(`/beatmapsets/${id}`);
            if (requestId !== state.beatmapDetail.detailRequestId || state.route !== "beatmap") return;
            renderBeatmapDetail(entry);
        } catch (error) {
            if (requestId !== state.beatmapDetail.detailRequestId || state.route !== "beatmap") return;
            if (cached) {
                renderBeatmapDetail(cached, "Показана информация из каталога — подробности временно недоступны.");
            } else {
                renderBeatmapDetail(
                    { id, title: `Карта #${id}`, artist: "osu!", creator: "неизвестен", status: "pending", beatmaps: [] },
                    error.status === 401
                        ? "Войдите, чтобы загрузить полную информацию. Ссылка на osu! доступна без входа."
                        : "Подробности временно недоступны. Можно открыть карту на osu!.",
                );
            }
        } finally {
            if (requestId === state.beatmapDetail.detailRequestId) loading.hidden = true;
        }
    }

    function beatmapScoreData(entry, index, page) {
        const score = entry.score || entry;
        const user = entry.user || score.user || {};
        return {
            score,
            user,
            rank: Number(entry.rank || entry.position || ((page - 1) * 50 + index + 1)),
            totalScore: Number(score.total_score || score.score || 0),
            pp: Number(score.pp || 0),
            accuracy: Number(score.accuracy || 0),
            combo: Number(score.max_combo || 0),
            grade: scoreGrade(score),
        };
    }

    function replayDownloadIcon() {
        const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.setAttribute("viewBox", "0 0 24 24");
        svg.setAttribute("aria-hidden", "true");
        ["M12 3v12", "m7 10 5 5 5-5", "M5 21h14"].forEach((definition) => {
            const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
            path.setAttribute("d", definition);
            svg.append(path);
        });
        return svg;
    }

    function renderBeatmapFilters() {
        const allowed = hasBeatmapSupporter();
        const detail = state.beatmapDetail;
        if (!allowed) { detail.scope = "global"; detail.mods = null; }
        $("#beatmap-supporter-notice").hidden = allowed;
        $$("[data-beatmap-scope]").forEach((button) => {
            button.setAttribute("aria-pressed", String(button.dataset.beatmapScope === detail.scope));
            button.setAttribute("aria-disabled", String(!allowed && button.dataset.beatmapScope !== "global"));
            const lock = button.querySelector(".supporter-lock");
            if (lock) lock.hidden = allowed;
        });
        const mode = detail.mode || "osu";
        const catalog = state.modCatalog[mode] || {};
        const mods = ["ALL", "NM", ...Object.keys(catalog)];
        $("#beatmap-mod-filters").replaceChildren(...mods.map((acronym) => {
            const button = make("button", "beatmap-mod-filter");
            button.type = "button";
            button.dataset.mod = acronym;
            const active = acronym === "ALL" ? detail.mods === null : detail.mods?.includes(acronym);
            button.setAttribute("aria-pressed", String(Boolean(active)));
            button.setAttribute("aria-disabled", String(!allowed && acronym !== "ALL"));
            button.setAttribute("aria-label", acronym === "ALL" ? "Все моды" : acronym === "NM" ? "Без модов" : `${catalog[acronym]?.name || acronym} (${acronym})`);
            button.title = button.getAttribute("aria-label");
            if (acronym === "ALL") button.textContent = "Все моды";
            else {
                const badge = make("span", `score-mod ${modCategoryClass({ruleset: mode}, acronym)}`);
                badge.append(make("b", "", acronym));
                button.append(badge);
            }
            button.addEventListener("click", () => {
                if (!allowed && acronym !== "ALL") { showBeatmapSupporterGate(); return; }
                if (acronym === "ALL") detail.mods = null;
                else if (acronym === "NM") detail.mods = detail.mods?.includes("NM") ? null : ["NM"];
                else {
                    const selected = (detail.mods || []).filter((mod) => mod !== "NM");
                    detail.mods = selected.includes(acronym) ? selected.filter((mod) => mod !== acronym) : [...selected, acronym];
                    if (!detail.mods.length) detail.mods = null;
                }
                detail.scorePage = 1;
                renderBeatmapFilters();
                busy(loadBeatmapScores());
            });
            return button;
        }));
    }

    function showBeatmapSupporterGate() {
        toast("Топ страны, друзей и фильтр по модам доступны с supporter.", "error");
        if (!state.session) openAuth("login");
    }

    function beatmapHitColumns() {
        const mode = state.beatmapDetail.mode;
        return mode === "mania"
            ? [["perfect", "MAX"], ["great", "300"], ["good", "200"], ["ok", "100"], ["meh", "50"], ["miss", "Промахи"]]
            : mode === "taiko" ? [["great", "GREAT"], ["ok", "OK"], ["miss", "Промахи"]]
            : [["great", "GREAT"], ["ok", "OK"], ["meh", "MEH"], ["miss", "Промахи"]];
    }

    function beatmapScoreUser(user, date = null) {
        const player = make("button", "beatmap-score-user");
        player.type = "button";
        const avatar = make("span", "avatar avatar-small");
        renderAvatar(avatar, user);
        const copy = make("span");
        copy.append(userNameNode(user));
        if (date) copy.append(make("small", "", formatRelativeDate(date)));
        copy.append(make("small", "", `${flagEmoji(user.country_code)} ${user.country_code || "XX"}`));
        player.append(avatar, copy);
        player.addEventListener("click", () => openProfile(userId(user)));
        return player;
    }

    function beatmapScoreActions(item) {
        const map = mapData(state.beatmapDetail.data || {});
        const difficulty = map.beatmaps.find((beatmap) => Number(beatmap.id) === Number(state.beatmapDetail.beatmapId)) || {};
        const actionableScore = {
            ...item.score, user: item.user,
            beatmap: {
                ...difficulty, beatmapset_id: map.id,
                beatmapset: { id: map.id, title: map.title, artist: map.artist, creator: map.creator, cover_url: map.cover },
            },
        };
        const root = make("div", "beatmap-score-actions");
        const replayUrl = replayUrlForScore(item.score);
        if (replayUrl) {
            const replay = make("a", "beatmap-score-replay");
            replay.href = replayUrl;
            replay.download = `score-${item.score.id}.osr`;
            replay.title = "Скачать реплей";
            replay.setAttribute("aria-label", `Скачать реплей ${userName(item.user)}`);
            replay.append(replayDownloadIcon());
            root.append(replay);
        }
        const menu = make("button", "score-menu", "⋮");
        menu.type = "button";
        menu.setAttribute("aria-label", `Действия с результатом #${item.score.id}`);
        menu.setAttribute("aria-haspopup", "menu");
        menu.setAttribute("aria-expanded", "false");
        menu.addEventListener("click", () => openScoreActionMenu(menu, actionableScore, { allowPin: false }));
        root.append(menu);
        return root;
    }

    function beatmapScoreMods(score) {
        const root = make("div", "beatmap-score-mods");
        const mods = scoreModList(score);
        if (!mods.length) root.append(make("span", "", "—"));
        mods.forEach((mod) => root.append(createScoreMod(mod, score)));
        return root;
    }

    function beatmapHighlight(entry, label) {
        const item = beatmapScoreData(entry, 0, 1);
        const root = make("article", `beatmap-highlight${item.score.negative_pp ? " is-negative-pp" : ""}`);
        root.setAttribute("aria-label", label);
        const position = make("div", "beatmap-highlight-position", `#${item.rank}`);
        position.append(make("span", `mini-grade is-${item.grade.toLowerCase()}`, item.grade));
        const identity = make("div");
        identity.append(beatmapScoreUser(item.user, item.score.ended_at), make("small", "", label));
        const numbers = make("div", "beatmap-highlight-numbers");
        const stat = (key, value, className = "") => {
            const node = make("div", `beatmap-highlight-stat ${className}`);
            node.append(make("small", "", key), make("strong", "", value));
            return node;
        };
        const primary = make("div", "beatmap-highlight-primary");
        primary.append(stat("Всего очков", formatNumber(item.totalScore)), stat("Точность", formatAccuracy(item.accuracy)), stat("Макс. комбо", `${formatNumber(item.combo)}x`));
        const secondary = make("div", "beatmap-highlight-secondary");
        beatmapHitColumns().forEach(([key, name]) => {
            const node = stat(name, formatNumber(item.score.statistics?.[key] || 0), "is-small");
            node.querySelector("strong").className = `beatmap-hit-${key}`;
            secondary.append(node);
        });
        secondary.append(stat("PP", formatNumber(item.pp), "is-small"), beatmapScoreMods(item.score), beatmapScoreActions(item));
        numbers.append(primary, secondary);
        root.append(position, identity, numbers);
        return root;
    }

    function renderBeatmapLeaderboard(data) {
        const items = getItems(data, "items", "scores");
        const pagination = getPages(data, state.beatmapDetail.scorePage);
        state.beatmapDetail.scorePage = pagination.page;
        state.beatmapDetail.scorePages = pagination.pages;
        const columns = beatmapHitColumns();
        const headers = ["Ранг", "", "Очки", "Точность", "Игрок", "Комбо", ...columns.map((column) => column[1]), "PP", "Время", "Моды", ""];
        const head = make("tr");
        headers.forEach((name) => head.append(make("th", "", name)));
        $("#beatmap-leaderboard-head").replaceChildren(head);
        const highlights = $("#beatmap-score-highlights");
        highlights.replaceChildren();
        if (data.top_score) highlights.append(beatmapHighlight(data.top_score, "Лучший результат"));
        if (data.user_score && data.user_score.score.id !== data.top_score?.score.id) highlights.append(beatmapHighlight(data.user_score, "Ваш лучший результат"));
        $("#beatmap-leaderboard-body").replaceChildren(...items.map((entry, index) => {
            const item = beatmapScoreData(entry, index, pagination.page);
            const row = make("tr", item.score.negative_pp ? "is-negative-pp" : "");
            const grade = make("td");
            grade.append(make("span", `mini-grade is-${item.grade.toLowerCase()}`, item.grade));
            const user = make("td");
            user.append(beatmapScoreUser(item.user));
            row.append(make("td", "ranking-position", `#${item.rank}`), grade,
                make("td", "beatmap-score-total", formatNumber(item.totalScore)),
                make("td", "", formatAccuracy(item.accuracy)), user,
                make("td", "", `${formatNumber(item.combo)}x`));
            columns.forEach(([key]) => row.append(make("td", `beatmap-hit-${key}`, formatNumber(item.score.statistics?.[key] || 0))));
            const age = make("td", "beatmap-score-age", formatRelativeDate(item.score.ended_at));
            age.title = formatDate(item.score.ended_at);
            const mods = make("td");
            mods.append(beatmapScoreMods(item.score));
            const actions = make("td", "beatmap-score-replay-cell");
            actions.append(beatmapScoreActions(item));
            row.append(make("td", "beatmap-score-pp", formatNumber(item.pp)), age, mods, actions);
            return row;
        }));
        $("#beatmap-leaderboard-empty").hidden = items.length > 0;
        $("#beatmap-scores-page").textContent = `${pagination.page} / ${pagination.pages}`;
        $("#beatmap-scores-prev").disabled = pagination.page <= 1;
        $("#beatmap-scores-next").disabled = pagination.page >= pagination.pages;
        $("#beatmap-leaderboard-pagination").hidden = pagination.pages <= 1;
    }

    async function loadBeatmapScores() {
        const detail = state.beatmapDetail;
        const beatmapId = Number(detail.beatmapId || 0);
        if (!beatmapId) return;
        const requestId = ++detail.scoreRequestId;
        $("#beatmap-leaderboard-body").replaceChildren();
        $("#beatmap-score-highlights").replaceChildren();
        $("#beatmap-leaderboard-empty").hidden = false;
        $("#beatmap-leaderboard-empty strong").textContent = "Загружаем рекорды…";
        $("#beatmap-leaderboard-empty p").textContent = "";
        try {
            const params = new URLSearchParams({ page: String(detail.scorePage), page_size: "50", scope: detail.scope, mode: detail.mode });
            if (detail.mods !== null) params.set("mods", detail.mods.join(","));
            const data = await api(`/beatmaps/${beatmapId}/scores?${params}`);
            if (requestId !== detail.scoreRequestId) return;
            renderBeatmapLeaderboard(data);
            $("#beatmap-leaderboard-empty strong").textContent = "Рекордов пока нет";
            $("#beatmap-leaderboard-empty p").textContent = detail.mods !== null || detail.scope !== "global" ? "Попробуйте другой фильтр." : "Станьте первым игроком на этой сложности.";
        } catch (error) {
            if (requestId !== detail.scoreRequestId) return;
            renderBeatmapLeaderboard({ items: [], page: 1, pages: 1 });
            $("#beatmap-leaderboard-empty strong").textContent = "Таблица временно недоступна";
            $("#beatmap-leaderboard-empty p").textContent = error.message || "Попробуйте обновить страницу чуть позже.";
        }
    }

    async function selectBeatmapDifficulty(beatmap, mode = state.beatmapDetail.mode || beatmapMode(beatmap?.mode)) {
        const beatmapId = Number(beatmap?.id || beatmap || 0);
        if (!beatmapId) return;
        if (!beatmapSupportsMode(beatmap, mode)) mode = beatmapMode(beatmap.mode);
        if (state.beatmapDetail.mode !== mode) state.beatmapDetail.mods = null;
        state.beatmapDetail.mode = mode;
        state.beatmapDetail.beatmapId = beatmapId;
        const beatmapsetId = Number(state.beatmapDetail.id || 0);
        if (state.route === "beatmap" && beatmapsetId) history.replaceState(null, "", `${location.pathname}${location.search}#beatmap/${beatmapsetId}/${beatmapId}/${mode}`);
        state.beatmapDetail.scorePage = 1;
        $("#beatmap-leaderboard").hidden = false;
        $("#beatmap-leaderboard-title").textContent = beatmap?.version || "Таблица лидеров";
        $("#beatmap-leaderboard-mode").textContent = `${detailModeLabel(mode)}${mode !== beatmapMode(beatmap.mode) ? " · Конверт" : ""} · SOMS!`;
        renderSelectedBeatmap(beatmap);
        await loadBeatmapScores();
    }

    async function loadBeatmapComments() {
        const detail = state.beatmapDetail;
        const requestId = ++detail.commentRequestId;
        const list = $("#beatmap-comment-list");
        list.replaceChildren(make("p", "beatmap-comment-empty", "Загружаем комментарии…"));
        try {
            const params = new URLSearchParams({ page: String(detail.commentPage), sort: detail.commentSort });
            const data = await api(`/beatmapsets/${detail.id}/comments?${params}`);
            if (requestId !== detail.commentRequestId) return;
            detail.commentPages = data.pages;
            $("#beatmap-comment-count").textContent = formatNumber(data.total);
            $("#beatmap-comments-page").textContent = `${data.page} / ${data.pages}`;
            $("#beatmap-comments-prev").disabled = data.page <= 1;
            $("#beatmap-comments-next").disabled = data.page >= data.pages;
            $("#beatmap-comment-pagination").hidden = data.pages <= 1;
            list.replaceChildren(...data.items.map((comment) => {
                const row = make("article", "beatmap-comment");
                const vote = make("button", "beatmap-comment-vote", `+${comment.votes}`);
                vote.type = "button";
                vote.setAttribute("aria-label", "Нравится комментарий");
                vote.setAttribute("aria-pressed", String(comment.voted));
                vote.addEventListener("click", async () => {
                    if (!state.session) { openAuth("login"); return; }
                    vote.disabled = true;
                    try {
                        await api(`/beatmap-comments/${comment.id}/vote`, { method: "PUT", data: { active: !comment.voted } });
                        await loadBeatmapComments();
                    } catch (error) { toast(error.message, "error"); vote.disabled = false; }
                });
                const avatar = make("span", "avatar");
                renderAvatar(avatar, comment.user);
                const copy = make("div");
                const user = make("button", "beatmap-comment-user");
                user.type = "button";
                renderUserName(user, comment.user);
                user.addEventListener("click", () => openProfile(userId(comment.user)));
                const footer = make("div", "beatmap-comment-footer");
                const time = make("time", "", formatRelativeDate(comment.created_at));
                time.dateTime = comment.created_at;
                time.title = formatDate(comment.created_at);
                footer.append(time);
                if (comment.can_delete) {
                    const remove = make("button", "", "Удалить");
                    remove.type = "button";
                    remove.addEventListener("click", async () => {
                        if (!window.confirm("Удалить этот комментарий?")) return;
                        remove.disabled = true;
                        try {
                            await api(`/beatmap-comments/${comment.id}`, { method: "DELETE" });
                            await loadBeatmapComments();
                        } catch (error) { toast(error.message, "error"); remove.disabled = false; }
                    });
                    footer.append(remove);
                }
                copy.append(user, make("p", "", comment.body), footer);
                row.append(vote, avatar, copy);
                return row;
            }));
            if (!data.items.length) list.append(make("p", "beatmap-comment-empty", "Пока никто не оставил комментарий. Будьте первым!"));
        } catch (error) {
            if (requestId === detail.commentRequestId) list.replaceChildren(make("p", "beatmap-comment-empty", "Не удалось загрузить комментарии. Попробуйте позже."));
        }
    }

    async function submitBeatmapComment(event) {
        event.preventDefault();
        if (!state.session) { openAuth("login"); return; }
        const input = $("#beatmap-comment-text"), body = input.value.trim();
        if (!body) return;
        const mapId = state.beatmapDetail.id;
        const button = $("#beatmap-comment-form button");
        button.disabled = true;
        try {
            await api(`/beatmapsets/${mapId}/comments`, { method: "POST", data: { body } });
            if (state.beatmapDetail.id !== mapId) return;
            input.value = "";
            state.beatmapDetail.commentPage = 1;
            state.beatmapDetail.commentSort = "new";
            $$("[data-comment-sort]").forEach((tab) => tab.setAttribute("aria-pressed", String(tab.dataset.commentSort === "new")));
            await loadBeatmapComments();
        } catch (error) { toast(error.message, "error"); }
        finally { button.disabled = false; }
    }

    function bindBeatmapPageEvents() {
        const audio = $("#beatmap-preview-audio");
        audio.volume = 0.1;
        const updatePreview = () => {
            const playing = !audio.paused;
            const button = $("#beatmap-preview-toggle");
            button.setAttribute("aria-pressed", String(playing));
            button.setAttribute("aria-label", playing ? "Остановить превью" : "Прослушать превью");
            button.replaceChildren(document.createTextNode(playing ? "❚❚ " : "▶ "), make("span", "", playing ? "Пауза" : "Прослушать"));
        };
        ["play", "pause", "ended"].forEach((event) => audio.addEventListener(event, updatePreview));
        audio.addEventListener("error", () => {
            updatePreview();
            if (state.route === "beatmap") toast("Превью временно недоступно", "error");
        });
        $("#beatmap-preview-toggle").addEventListener("click", async () => {
            if (!audio.paused) { audio.pause(); return; }
            try { await audio.play(); }
            catch { toast("Не удалось включить превью. Попробуйте ещё раз.", "error"); }
        });
        $$("[data-beatmap-scope]").forEach((button) => button.addEventListener("click", () => {
            if (button.dataset.beatmapScope !== "global" && !hasBeatmapSupporter()) { showBeatmapSupporterGate(); return; }
            state.beatmapDetail.scope = button.dataset.beatmapScope;
            state.beatmapDetail.scorePage = 1;
            renderBeatmapFilters();
            busy(loadBeatmapScores());
        }));
        $("#beatmap-comment-form").addEventListener("submit", submitBeatmapComment);
        $("#beatmap-comment-text").addEventListener("keydown", (event) => {
            if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                if (!$("#beatmap-comment-form button").disabled) $("#beatmap-comment-form").requestSubmit();
            }
        });
        $$("[data-comment-sort]").forEach((button) => button.addEventListener("click", () => {
            state.beatmapDetail.commentSort = button.dataset.commentSort;
            state.beatmapDetail.commentPage = 1;
            $$("[data-comment-sort]").forEach((tab) => tab.setAttribute("aria-pressed", String(tab === button)));
            loadBeatmapComments();
        }));
        $("#beatmap-comments-prev").addEventListener("click", () => {
            if (state.beatmapDetail.commentPage <= 1) return;
            state.beatmapDetail.commentPage -= 1;
            loadBeatmapComments();
        });
        $("#beatmap-comments-next").addEventListener("click", () => {
            if (state.beatmapDetail.commentPage >= state.beatmapDetail.commentPages) return;
            state.beatmapDetail.commentPage += 1;
            loadBeatmapComments();
        });
    }

    const rankingViews = Object.freeze({
        world: {
            subtitle: { performance: "по производительности", score: "по рейтинговым очкам" },
            sorts: { performance: "Производительность", score: "Рейтинговые очки" },
            empty: ["Рейтинг ещё не начался", "Сыграйте рейтинговую карту и станьте первым."],
        },
        countries: {
            subtitle: { performance: "страны по производительности", score: "страны по рейтинговым очкам" },
            sorts: { performance: "Производительность", score: "Рейтинговые очки" },
            empty: ["Страны ещё не попали в рейтинг", "Рейтинг появится после первых результатов игроков."],
        },
        scores: {
            subtitle: { performance: "лучшие рекорды по производительности", score: "лучшие рекорды по очкам" },
            sorts: { performance: "Производительность", score: "Результат" },
            empty: ["Лучших рекордов пока нет", "Завершите рейтинговую карту, чтобы результат появился здесь."],
        },
        teams: {
            subtitle: { performance: "команды по производительности", score: "команды по рейтинговым очкам" },
            sorts: { performance: "Производительность", score: "Рейтинговые очки" },
            empty: ["Команд в рейтинге пока нет", "Команда появится после рейтинговых игр её участников."],
        },
        playlists: {
            subtitle: { performance: "по производительности в плейлистах", score: "по очкам в плейлистах" },
            sorts: { performance: "Производительность", score: "Сумма очков" },
            empty: ["Результатов плейлистов пока нет", "Сыграйте плейлист в мультиплеере, чтобы попасть в таблицу."],
        },
        somsai: {
            subtitle: { performance: "по MMR SOMSAI", score: "по MMR SOMSAI" },
            sorts: { performance: "MMR SOMSAI", score: "MMR SOMSAI" },
            empty: ["Рейтинг SOMSAI ещё не начался", "Зайдите в SOMSAI в клиенте, чтобы получить начальный рейтинг."],
        },
        daily: {
            subtitle: { performance: "по серии карт дня", score: "по числу сыгранных дней" },
            sorts: { performance: "Текущая серия", score: "Дней сыграно" },
            empty: ["Карт дня пока не сыграно", "Завершите ежедневное испытание, чтобы попасть в таблицу."],
        },
    });

    function rankingEntryData(entry, index, page) {
        const user = unwrapUser(entry);
        const statistics = entry.statistics || user.statistics || {};
        return {
            user,
            rank: Number(entry.rank || entry.global_rank || statistics.global_rank || ((page - 1) * 50 + index + 1)),
            pp: statValue(entry, "pp", "performance") || statValue(statistics, "pp"),
            accuracy: statValue(entry, "accuracy", "hit_accuracy") || statValue(statistics, "accuracy", "hit_accuracy"),
            playCount: statValue(entry, "play_count", "plays") || statValue(statistics, "play_count"),
            rankedScore: statValue(entry, "ranked_score") || statValue(statistics, "ranked_score"),
            gradeCounts: entry.grade_counts || statistics.grade_counts || {},
        };
    }

    function rankingCountryName(code) {
        const normalized = String(code || "XX").toUpperCase();
        return regionNames?.of(normalized) || normalized;
    }

    function rankingPlayerCell(user) {
        const identityCell = make("td");
        const player = make("div", "player-cell");
        const avatar = make("span", "avatar avatar-small");
        renderAvatar(avatar, user);
        const copy = make("span");
        copy.append(
            userNameNode(user),
            make("small", "", `${flagEmoji(user.country_code)} ${user.country_code || "XX"} · ID ${userId(user)}`),
        );
        player.append(avatar, copy);
        identityCell.append(player);
        return identityCell;
    }

    function setRankingRowAction(row, action = null) {
        if (!action) {
            row.className = "ranking-row-static";
            return;
        }
        row.className = "ranking-row-user";
        row.tabIndex = 0;
        row.addEventListener("click", action);
        row.addEventListener("keydown", (event) => {
            if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                action();
            }
        });
    }

    function setRankingHeaders(headers, kind) {
        const row = make("tr");
        headers.forEach((header) => row.append(make("th", "", header)));
        $("#rankings-head").replaceChildren(row);
        $("#ranking-table").dataset.kind = kind;
    }

    function updateRankingControls() {
        const rankings = state.rankings;
        const view = rankingViews[rankings.section] || rankingViews.world;
        $$('[data-ranking-section]').forEach((button) => {
            const active = button.dataset.rankingSection === rankings.section;
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-pressed", String(active));
        });
        $$('[data-ranking-sort]').forEach((button) => {
            const sort = button.dataset.rankingSort;
            const active = sort === rankings.sort;
            button.textContent = view.sorts[sort];
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-pressed", String(active));
        });
        $$('[data-ranking-scope]').forEach((button) => {
            const active = button.dataset.rankingScope === rankings.scope;
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-pressed", String(active));
        });
        $("#ranking-subtitle").textContent = view.subtitle[rankings.sort];
        $("#ranking-country").value = rankings.country;
        $("#ranking-somsai-filter").hidden = rankings.section !== "somsai";
        $("#ranking-somsai-variant").hidden = state.mode !== "mania";
        $('[data-ranking-sort="score"]').hidden = rankings.section === "somsai";
        $("#ranking-country-filter").hidden = ["countries", "teams"].includes(rankings.section);
        $("#ranking-scope-filter").hidden = ["countries", "teams"].includes(rankings.section);
        $("#rankings-empty strong").textContent = view.empty[0];
        $("#rankings-empty p").textContent = view.empty[1];
    }

    function populateRankingCountries() {
        const select = $("#ranking-country");
        if (select.options.length > 1) return;
        ISO_CODES
            .map((code) => ({ code, name: rankingCountryName(code) }))
            .sort((left, right) => left.name.localeCompare(right.name, "ru"))
            .forEach(({ code, name }) => {
                const option = make("option", "", `${flagEmoji(code)} ${name}`);
                option.value = code;
                select.append(option);
            });
    }

    async function loadRankings() {
        const rankings = state.rankings;
        rankings.controller?.abort();
        const controller = new AbortController();
        const requestId = ++rankings.requestId;
        rankings.controller = controller;
        populateRankingCountries();
        updateRankingControls();
        const params = new URLSearchParams({
            mode: state.mode,
            section: rankings.section,
            somsai_format: $("#ranking-somsai-format").value,
            somsai_variant: $("#ranking-somsai-variant").value,
            sort: rankings.sort,
            scope: rankings.scope,
            page: String(rankings.page),
        });
        if (rankings.country && !["countries", "teams"].includes(rankings.section)) {
            params.set("country", rankings.country);
        }
        let data;
        try {
            data = await api(`/rankings?${params}`, { signal: controller.signal });
        } catch (error) {
            if (error.name === "AbortError") return;
            throw error;
        }
        if (requestId !== rankings.requestId) return;
        const items = getItems(data, "items", "rankings", "users");
        const pagination = getPages(data, rankings.page);
        rankings.page = pagination.page;
        rankings.pages = pagination.pages;
        renderPodium([]);
        renderRankingTable(items, pagination.page, data.section || rankings.section);
        $("#rankings-empty").hidden = items.length > 0;
        $("#ranking-table").hidden = items.length === 0;
        $("#rankings-page").textContent = `Страница ${pagination.page} из ${pagination.pages}`;
        $("#rankings-prev").disabled = pagination.page <= 1;
        $("#rankings-next").disabled = pagination.page >= pagination.pages;
    }

    function renderPodium(items) {
        const root = $("#ranking-podium");
        root.replaceChildren();
        root.hidden = items.length === 0;
        items.forEach((entry, index) => {
            const data = rankingEntryData(entry, index, 1);
            const person = make("button", `podium-person is-${["first", "second", "third"][index]}`);
            person.type = "button";
            const avatar = make("span", "podium-avatar avatar", initials(userName(data.user)));
            renderAvatar(avatar, data.user);
            avatar.append(make("span", "podium-place", index + 1));
            person.append(avatar, userNameNode(data.user), make("span", "", `${formatNumber(data.pp)} pp`));
            person.addEventListener("click", () => openProfile(userId(data.user)));
            root.append(person);
        });
    }

    function renderRankingTable(items, page, section = "world") {
        const body = $("#rankings-body");
        body.replaceChildren();
        const headers = {
            world: ["#", "Игрок", "Точность", "Количество игр", "Рейтинговые очки", "Производительность", "SS", "S", "A"],
            countries: ["#", "Страна", "Игроков", "Количество игр", "Рейтинговые очки", "Производительность"],
            scores: ["#", "Карта", "Игрок", "Оценка", "Точность", "Результат", "PP", "Дата"],
            teams: ["#", "Команда", "Участников", "Количество игр", "Рейтинговые очки", "Производительность"],
            playlists: ["#", "Игрок", "Точность", "Завершено", "Попытки", "Сумма очков", "Производительность"],
            somsai: ["#", "Игрок", "MMR SOMSAI", "Победы", "Матчи"],
            daily: ["#", "Игрок", "Текущая серия", "Лучшая серия", "Дней сыграно", "Сумма очков", "Производительность"],
        };
        setRankingHeaders(headers[section] || headers.world, section);

        if (section === "countries") {
            items.forEach((item) => {
                const row = make("tr");
                const countryCell = make("td");
                const country = make("span", "ranking-country-cell");
                country.append(make("b", "", flagEmoji(item.code)), make("span", "", item.name || rankingCountryName(item.code)));
                countryCell.append(country);
                row.append(
                    make("td", "ranking-position", item.rank),
                    countryCell,
                    make("td", "", formatNumber(item.active_users)),
                    make("td", "", formatNumber(item.play_count)),
                    make("td", "", formatNumber(item.ranked_score)),
                    make("td", "", `${formatNumber(item.pp)} pp`),
                );
                setRankingRowAction(row, () => {
                    state.rankings.section = "world";
                    state.rankings.country = item.code;
                    state.rankings.page = 1;
                    $("#ranking-country").value = item.code;
                    busy(loadRankings()).catch((error) => toast(error.message, "error"));
                });
                body.append(row);
            });
            return;
        }

        if (section === "scores") {
            items.forEach((item) => {
                const row = make("tr", item.negative_pp ? "is-negative-pp" : "");
                const mapCell = make("td");
                const mapCopy = make("span", "ranking-score-cell");
                mapCopy.append(
                    make("strong", "", item.beatmapset?.title || `Карта #${item.beatmap?.id || 0}`),
                    make("small", "", `${item.beatmapset?.artist || "—"} · ${item.beatmap?.version || "—"}`),
                );
                mapCell.append(mapCopy);
                row.append(
                    make("td", "ranking-position", item.rank_position),
                    mapCell,
                    rankingPlayerCell(item.user || {}),
                    make("td", "grade-count", item.rank || "—"),
                    make("td", "", formatAccuracy(item.accuracy)),
                    make("td", "", formatNumber(item.total_score)),
                    make("td", "", `${formatNumber(item.pp)} pp`),
                    make("td", "", item.ended_at ? shortDateFormat.format(new Date(item.ended_at)) : "—"),
                );
                setRankingRowAction(row, () => openBeatmap(item.beatmapset?.id, item.beatmap?.id));
                body.append(row);
            });
            return;
        }

        if (section === "teams") {
            items.forEach((item) => {
                const row = make("tr");
                const teamCell = make("td");
                const team = make("span", "ranking-team-cell");
                team.append(
                    make("b", "ranking-team-badge", item.team?.short_name || "?"),
                    make("strong", "", item.team?.name || `Команда #${item.team?.id || 0}`),
                );
                teamCell.append(team);
                row.append(
                    make("td", "ranking-position", item.rank),
                    teamCell,
                    make("td", "", formatNumber(item.member_count)),
                    make("td", "", formatNumber(item.play_count)),
                    make("td", "", formatNumber(item.ranked_score)),
                    make("td", "", `${formatNumber(item.pp)} pp`),
                );
                setRankingRowAction(row);
                body.append(row);
            });
            return;
        }

        if (["playlists", "daily", "somsai"].includes(section)) {
            items.forEach((item) => {
                const row = make("tr");
                const cells = [make("td", "ranking-position", item.rank), rankingPlayerCell(item.user || {})];
                if (section === "somsai") {
                    cells.push(
                        make("td", "", formatNumber(item.mmr)),
                        make("td", "", formatNumber(item.wins)),
                        make("td", "", formatNumber(item.games)),
                    );
                } else if (section === "daily") {
                    cells.push(
                        make("td", "", formatNumber(item.daily_streak_current)),
                        make("td", "", formatNumber(item.daily_streak_best)),
                        make("td", "", formatNumber(item.play_count)),
                        make("td", "", formatNumber(item.total_score)),
                        make("td", "", `${formatNumber(item.pp)} pp`),
                    );
                } else {
                    cells.push(
                        make("td", "", formatAccuracy(item.accuracy)),
                        make("td", "", formatNumber(item.completed)),
                        make("td", "", formatNumber(item.attempts)),
                        make("td", "", formatNumber(item.total_score)),
                        make("td", "", `${formatNumber(item.pp)} pp`),
                    );
                }
                row.append(...cells);
                setRankingRowAction(row, () => openProfile(userId(item.user)));
                body.append(row);
            });
            return;
        }

        items.forEach((entry, index) => {
            const data = rankingEntryData(entry, index, page);
            const row = make("tr");
            row.append(
                make("td", "ranking-position", data.rank),
                rankingPlayerCell(data.user),
                make("td", "", formatAccuracy(data.accuracy)),
                make("td", "", formatNumber(data.playCount)),
                make("td", "", formatNumber(data.rankedScore)),
                make("td", "", formatNumber(data.pp)),
                make("td", "grade-count", formatNumber(statValue(data.gradeCounts, "ss") + statValue(data.gradeCounts, "ssh"))),
                make("td", "grade-count", formatNumber(statValue(data.gradeCounts, "s") + statValue(data.gradeCounts, "sh"))),
                make("td", "grade-count", formatNumber(statValue(data.gradeCounts, "a"))),
            );
            setRankingRowAction(row, () => openProfile(userId(data.user)));
            body.append(row);
        });
    }

    async function loadBeatmaps() {
        const beatmaps = state.beatmaps;
        beatmaps.controller?.abort();
        const controller = new AbortController();
        const requestId = ++beatmaps.requestId;
        beatmaps.controller = controller;
        const page = beatmaps.page;
        const cursor = beatmaps.cursorHistory[page - 1] || null;
        beatmaps.nextCursor = null;
        updateBeatmapPagination(true);
        const params = new URLSearchParams({
            q: beatmaps.query,
            mode: state.mode,
            status: beatmaps.status,
            sort: beatmaps.sort,
        });
        if (cursor) params.set("cursor_string", cursor);
        try {
            const data = await api(`/beatmapsets?${params}`, { signal: controller.signal });
            if (requestId !== beatmaps.requestId) return false;
            const items = getItems(data, "items", "beatmapsets", "maps");
            const candidate = typeof data.cursor_string === "string" && data.cursor_string.length > 0
                ? data.cursor_string
                : null;
            beatmaps.nextCursor = candidate && candidate !== cursor ? candidate : null;
            const rendered = renderMapCards($("#beatmaps-grid"), items);
            $("#beatmaps-empty").hidden = rendered > 0;
            return true;
        } catch (error) {
            if (error.name === "AbortError" || requestId !== beatmaps.requestId) return false;
            throw error;
        } finally {
            if (requestId === beatmaps.requestId) {
                beatmaps.controller = null;
                updateBeatmapPagination(false);
            }
        }
    }

    async function navigateBeatmaps(direction) {
        const beatmaps = state.beatmaps;
        const previousPage = beatmaps.page;
        const previousHistory = beatmaps.cursorHistory;
        const previousNextCursor = beatmaps.nextCursor;
        if (direction < 0) {
            if (beatmaps.page <= 1) return;
            beatmaps.page -= 1;
        } else {
            if (!beatmaps.nextCursor) return;
            beatmaps.cursorHistory = beatmaps.cursorHistory.slice(0, beatmaps.page);
            beatmaps.cursorHistory.push(beatmaps.nextCursor);
            beatmaps.page += 1;
        }
        beatmaps.nextCursor = null;
        updateBeatmapPagination(true);
        try {
            await loadBeatmaps();
        } catch (error) {
            beatmaps.page = previousPage;
            beatmaps.cursorHistory = previousHistory;
            beatmaps.nextCursor = previousNextCursor;
            updateBeatmapPagination(false);
            throw error;
        }
    }

    function currentStatistics(payload, user) {
        const raw = payload.statistics || payload.stats || user.statistics || {};
        if (Array.isArray(raw)) return raw.find((item) => String(item.mode) === state.mode) || raw[0] || {};
        if (raw[state.mode]) return raw[state.mode];
        return raw;
    }

    async function loadProfile() {
        const requestId = ++state.profile.requestId;
        const id = state.profile.id || userId(state.session?.user);
        if (!id) {
            $("#profile-guest").hidden = false;
            $("#profile-content").hidden = true;
            return;
        }
        $("#profile-guest").hidden = true;
        $("#profile-content").hidden = false;
        const scorePageSize = state.profile.scoreType === "best" ? state.profile.scoreLimit : 20;
        const sessionUser = state.session?.user;
        const requestedProfileId = Number(id);
        const ownProfile = Boolean(sessionUser) && [Number(sessionUser.id), userId(sessionUser)].includes(requestedProfileId);
        const achievementParams = new URLSearchParams({ latest_limit: "8", mode: state.mode });
        const achievementsPath = ownProfile
            ? `/me/achievements?${achievementParams}`
            : `/users/${id}/achievements?${achievementParams}`;
        const [profile, scores, pinnedScores, achievements, activityScores, firstScores] = await Promise.all([
            api(`/users/${id}?${new URLSearchParams({ mode: state.mode, somsai_variant: String(state.profile.somsaiVariant), somsai_format: state.profile.somsaiFormat })}`),
            api(`/users/${id}/scores?${new URLSearchParams({ mode: state.mode, type: state.profile.scoreType, page: String(state.profile.page), page_size: String(scorePageSize) })}`),
            api(`/users/${id}/scores?${new URLSearchParams({ mode: state.mode, type: "pinned", page: "1", page_size: "50" })}`).catch((error) => ({ unsupported: [404, 405, 422, 501].includes(error.status), items: [] })),
            api(achievementsPath).catch(() => ({ unavailable: true, total: 0, unlocked_count: 0, catalog_visible: ownProfile, latest: [], groups: [] })),
            api(`/users/${id}/scores?${new URLSearchParams({ mode: state.mode, type: "recent", page: "1", page_size: "50" })}`).catch(() => ({ items: [] })),
            api(`/users/${id}/scores?${new URLSearchParams({ mode: state.mode, type: "first", page: "1", page_size: "5" })}`),
        ]);
        const user = profile.user || profile;
        const own = Boolean(state.session?.user) && Number(state.session.user.id) === Number(user.id);
        let friendship = null;
        if (state.session?.user && !own) {
            friendship = await api(`/users/${user.server_id || user.id}/friendship`);
        }
        if (requestId !== state.profile.requestId || state.route !== "profile") return;
        state.profile.user = user;
        state.profile.payload = profile;
        state.profile.friendship = friendship;
        state.profile.activityScores = getItems(activityScores, "items", "scores");
        state.profile.pinnedScores = getItems(pinnedScores, "items", "scores");
        state.profile.pinningSupported = pinnedScores?.unsupported !== true;
        renderProfile(profile, user, state.profile.activityScores);
        renderPinnedScores(pinnedScores);
        renderProfileScores(scores);
        $("#profile-first-count").textContent = formatNumber(firstScores.total);
        state.profile.firstScorePage = 1;
        $("#profile-first-scores").replaceChildren(...getItems(firstScores, "items").map((score) => createScoreRow(score, 1)));
        $("#first-scores-more").hidden = firstScores.pages <= 1;
        renderProfileAchievements(achievements);
        renderProfileActivity(profile, state.profile.activityScores);
        applyProfileOrder(profile.profile_order || user.profile_order);
    }

    function renderProfile(profile, user, activityScores = []) {
        renderUserName($("#profile-name"), user);
        const supporter = Boolean(user.is_supporter || (user.roles || []).includes("supporter"));
        $("#profile-supporter-heart").hidden = !supporter;
        $("#profile-country").textContent = `${flagEmoji(user.country_code)} ${user.country_code || "XX"}`;
        const profileTitle = $("#profile-title");
        const roleLabels = (user.roles || [])
            .filter((role) => role !== "supporter")
            .map((role) => PROFILE_ROLE_LABELS[role] || role);
        profileTitle.replaceChildren(...roleLabels.map((label) => make("span", "profile-role", label)));
        profileTitle.hidden = roleLabels.length === 0;
        renderAvatar($("#profile-avatar"), user);
        renderProfileMetadata(user);
        const own = Number(state.session?.user?.id) === Number(user.id);
        renderProfileFriendship(profile, user, own);
        $("#profile-settings-button").hidden = !own;
        $("#profile-avatar-edit").hidden = !own;
        $("#userpage-edit").hidden = !own;
        $$(".movable-profile-block").forEach((block) => {
            block.draggable = own;
            block.classList.toggle("is-owned", own);
        });
        $$(".block-move-actions").forEach((actions) => { actions.hidden = !own; });

        const stats = currentStatistics(profile, user);
        const globalRank = statValue(stats, "global_rank", "rank");
        const countryRank = statValue(stats, "country_rank");
        $("#profile-global-rank").textContent = globalRank ? `#${formatNumber(globalRank)}` : "—";
        $("#profile-country-rank").textContent = countryRank ? `#${formatNumber(countryRank)}` : "—";
        const somsai = profile.somsai || {};
        $("#profile-somsai-rank").textContent = somsai.global_rank ? `#${formatNumber(somsai.global_rank)}` : "—";
        $("#profile-somsai-variant").hidden = state.mode !== "mania";
        $("#profile-somsai-variant").value = String(state.profile.somsaiVariant);
        $("#profile-mmr").textContent = somsai.mmr == null ? "—" : formatNumber(Math.round(somsai.mmr));
        $("#profile-pp").textContent = formatNumber(statValue(stats, "pp"));
        $("#profile-play-time").textContent = formatPlayTime(statValue(stats, "play_time"));
        const level = Math.max(1, Number(statValue(stats, "level") || 1));
        $("#profile-level").textContent = String(Math.floor(level));
        $("#profile-level-bar").style.width = `${Math.max(2, Math.min(100, (level % 1) * 100))}%`;

        const statsRoot = $("#profile-stats");
        const playCount = Number(statValue(stats, "play_count") || 0);
        const totalHits = Number(statValue(stats, "total_hits") || 0);
        const statRows = [
            ["Рейтинговые очки", formatNumber(statValue(stats, "ranked_score"))],
            ["Точность попаданий", formatAccuracy(statValue(stats, "accuracy", "hit_accuracy"))],
            ["Количество игр", formatNumber(playCount)],
            ["Всего очков", formatNumber(statValue(stats, "total_score"))],
            ["Всего попаданий", formatNumber(totalHits)],
            ["Попаданий в среднем за игру", formatNumber(playCount ? Math.round(totalHits / playCount) : 0)],
            ["Максимальное комбо", `${formatNumber(statValue(stats, "maximum_combo"))}x`],
        ];
        statsRoot.replaceChildren(...statRows.map(([label, value]) => {
            const row = make("div");
            row.append(make("dt", "", label), make("dd", "", value));
            return row;
        }));
        const gradeCounts = stats.grade_counts || {};
        const gradeRoot = $("#profile-grade-counts");
        const grades = [
            ["SSH", statValue(gradeCounts, "ssh", "xh"), "is-ssh"],
            ["SS", statValue(gradeCounts, "ss", "x"), "is-ss"],
            ["SH", statValue(gradeCounts, "sh"), "is-sh"],
            ["S", statValue(gradeCounts, "s"), "is-s"],
            ["A", statValue(gradeCounts, "a"), "is-a"],
            ["B", statValue(gradeCounts, "b"), "is-b"],
            ["C", statValue(gradeCounts, "c"), "is-c"],
            ["D", statValue(gradeCounts, "d"), "is-d"],
        ];
        gradeRoot.replaceChildren(...grades.map(([grade, count, className]) => {
            const counter = make("span", "grade-counter");
            const pill = make("span", `grade-pill ${className}`.trim(), grade);
            pill.title = `${grade}: ${formatNumber(count)}`;
            counter.append(pill, make("small", "", formatNumber(count)));
            return counter;
        }));
        renderRankHistory(profile.rank_history || profile.history || user.rank_history || [], globalRank, activityScores);
        renderProfileUserpage(profile, user, own);
    }

    function profileFriendIcon(stateName) {
        const paths = {
            none: [
                "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z",
                "M2 21a7 7 0 0 1 14 0",
                "M19 5v7",
                "M15.5 8.5h7",
            ],
            following: [
                "M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z",
                "M4 21a8 8 0 0 1 16 0",
            ],
            mutual: [
                "M8.5 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z",
                "M1.5 21a7 7 0 0 1 12.5-4.3",
                "M16.5 12a3 3 0 1 0 0-6",
                "M13 16.5A6 6 0 0 1 22.5 21",
            ],
        };
        const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.setAttribute("viewBox", "0 0 24 24");
        svg.setAttribute("aria-hidden", "true");
        paths[stateName].forEach((definition) => {
            const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
            path.setAttribute("d", definition);
            svg.append(path);
        });
        return svg;
    }

    function renderProfileFriendship(profile, user, own = false) {
        const slot = $("#profile-friend-slot");
        const friendship = state.profile.friendship || {};
        const stateName = friendship.mutual ? "mutual" : friendship.is_following ? "following" : "none";
        const followerCount = Math.max(
            0,
            Number(friendship.follower_count ?? user.follower_count ?? profile.follower_count ?? 0),
        );
        const button = $("#profile-friend-button");
        slot.hidden = false;
        if (own) {
            button.dataset.state = "none";
            button.classList.add("is-own");
            button.dataset.tooltip = "Друзья";
            button.setAttribute("aria-disabled", "true");
            button.setAttribute("aria-pressed", "false");
            button.setAttribute("aria-label", `Друзья. Подписчиков: ${formatNumber(followerCount)}`);
            button.removeAttribute("title");
            $("#profile-friend-icon").replaceChildren(profileFriendIcon("mutual"));
            $("#profile-follower-count").textContent = formatNumber(followerCount);
            return;
        }
        button.classList.remove("is-own");
        button.removeAttribute("aria-disabled");
        button.removeAttribute("data-tooltip");
        button.removeAttribute("title");
        button.dataset.state = stateName;
        button.setAttribute("aria-pressed", String(Boolean(friendship.is_following)));
        button.setAttribute(
            "aria-label",
            friendship.is_following
                ? `Удалить ${user.username} из друзей. Подписчиков: ${formatNumber(followerCount)}`
                : `Добавить ${user.username} в друзья. Подписчиков: ${formatNumber(followerCount)}`,
        );
        $("#profile-friend-icon").replaceChildren(profileFriendIcon(stateName));
        $("#profile-follower-count").textContent = formatNumber(followerCount);
    }

    async function toggleProfileFriendship() {
        const user = state.profile.user;
        if (!user) return;
        if (!state.session?.user) {
            toast("Войди, чтобы добавлять игроков в друзья", "error");
            openAuth("login");
            return;
        }
        if (Number(state.session.user.id) === Number(user.id)) return;

        const button = $("#profile-friend-button");
        const wasFollowing = Boolean(state.profile.friendship?.is_following);
        button.disabled = true;
        button.setAttribute("aria-busy", "true");
        try {
            const friendship = await api(`/users/${user.server_id || user.id}/friendship`, {
                method: wasFollowing ? "DELETE" : "PUT",
            });
            state.profile.friendship = friendship;
            user.follower_count = friendship.follower_count;
            renderProfileFriendship(state.profile.payload || {}, user);
            toast(
                wasFollowing
                    ? `${user.username} удалён из друзей`
                    : friendship.mutual
                        ? `Теперь вы с ${user.username} взаимные друзья`
                        : `${user.username} добавлен в друзья`,
            );
        } catch (error) {
            toast(error.message, "error");
        } finally {
            button.disabled = false;
            button.removeAttribute("aria-busy");
        }
    }

    function formatPlayTime(seconds) {
        const minutes = Math.max(0, Math.floor(Number(seconds || 0) / 60));
        const hours = Math.floor(minutes / 60);
        const days = Math.floor(hours / 24);
        return `${days ? `${days} дн. ` : ""}${hours % 24} ч. ${minutes % 60} мин.`;
    }

    function formatMapLength(seconds) {
        const total = Math.max(0, Math.floor(Number(seconds || 0)));
        const minutes = Math.floor(total / 60);
        return `${minutes}:${String(total % 60).padStart(2, "0")}`;
    }

    function readLocalJson(key) {
        try {
            const value = localStorage.getItem(key);
            return value ? JSON.parse(value) : null;
        } catch {
            return null;
        }
    }

    function writeLocalJson(key, value) {
        try {
            localStorage.setItem(key, JSON.stringify(value));
            return true;
        } catch {
            return false;
        }
    }

    function removeLocalValue(key) {
        try { localStorage.removeItem(key); } catch { /* Storage can be unavailable in private mode. */ }
    }

    function appendBbcode(target, raw) {
        const text = String(raw || "");
        const pattern = /\[img\]([\s\S]*?)\[\/img\]|\[url=([^\]]+)\]|\[\/url\]|\[(\/?)(b|i|u|s|quote|center|code)\]|\r?\n/gi;
        const stack = [{ name: "root", node: target }];
        const current = () => stack[stack.length - 1].node;
        let cursor = 0;
        let match;
        while ((match = pattern.exec(text)) !== null) {
            if (match.index > cursor) current().append(document.createTextNode(text.slice(cursor, match.index)));
            const token = match[0].toLowerCase();
            if (match[1] !== undefined) {
                const url = safeAssetUrl(match[1].trim());
                if (url) {
                    const image = make("img", "userpage-image");
                    image.src = url;
                    image.alt = "Изображение профиля";
                    image.loading = "lazy";
                    image.referrerPolicy = "no-referrer";
                    image.addEventListener("error", () => image.remove(), { once: true });
                    current().append(image);
                }
            } else if (match[2] !== undefined) {
                const url = safeAssetUrl(match[2].trim());
                const link = make(url ? "a" : "span");
                if (url) {
                    link.href = url;
                    link.target = "_blank";
                    link.rel = "noopener noreferrer nofollow";
                }
                current().append(link);
                stack.push({ name: "url", node: link });
            } else if (token === "[/url]") {
                const index = stack.map((item) => item.name).lastIndexOf("url");
                if (index > 0) stack.splice(index);
            } else if (token === "\n" || token === "\r\n") {
                current().append(make("br"));
            } else {
                const closing = match[3] === "/";
                const name = String(match[4] || "").toLowerCase();
                if (closing) {
                    const index = stack.map((item) => item.name).lastIndexOf(name);
                    if (index > 0) stack.splice(index);
                } else {
                    const tags = { b: "strong", i: "em", u: "u", s: "s", quote: "blockquote", center: "div", code: "code" };
                    const node = make(tags[name] || "span", name === "center" ? "userpage-center" : "");
                    current().append(node);
                    stack.push({ name, node });
                }
            }
            cursor = pattern.lastIndex;
        }
        if (cursor < text.length) current().append(document.createTextNode(text.slice(cursor)));
    }

    function appendSanitizedHtml(target, html) {
        const parsed = new DOMParser().parseFromString(String(html || ""), "text/html");
        const allowed = new Set(["P", "BR", "STRONG", "B", "EM", "I", "U", "S", "BLOCKQUOTE", "UL", "OL", "LI", "H1", "H2", "H3", "CODE", "PRE", "DIV", "SPAN", "A", "IMG"]);
        const copy = (source, destination) => {
            source.childNodes.forEach((child) => {
                if (child.nodeType === Node.TEXT_NODE) {
                    destination.append(document.createTextNode(child.textContent || ""));
                    return;
                }
                if (child.nodeType !== Node.ELEMENT_NODE) return;
                if (!allowed.has(child.tagName)) {
                    copy(child, destination);
                    return;
                }
                if (child.tagName === "IMG") {
                    const url = safeAssetUrl(child.getAttribute("src"));
                    if (!url) return;
                    const image = make("img", "userpage-image");
                    image.src = url;
                    image.alt = String(child.getAttribute("alt") || "Изображение профиля").slice(0, 160);
                    image.loading = "lazy";
                    image.referrerPolicy = "no-referrer";
                    image.addEventListener("error", () => image.remove(), { once: true });
                    destination.append(image);
                    return;
                }
                const element = document.createElement(child.tagName.toLowerCase());
                if (child.tagName === "A") {
                    const url = safeAssetUrl(child.getAttribute("href"));
                    if (!url) {
                        copy(child, destination);
                        return;
                    }
                    element.href = url;
                    element.target = "_blank";
                    element.rel = "noopener noreferrer nofollow";
                }
                copy(child, element);
                destination.append(element);
            });
        };
        copy(parsed.body, target);
    }

    function renderSafeUserpage(root, body, sanitizedHtml = "") {
        root.replaceChildren();
        if (sanitizedHtml) appendSanitizedHtml(root, sanitizedHtml);
        else appendBbcode(root, body);
        root.classList.toggle("is-empty", !root.textContent.trim() && !root.querySelector("img"));
        if (root.classList.contains("is-empty")) root.append(make("p", "muted-copy", "Игрок пока ничего здесь не написал."));
    }

    function userpageStorageKey(userIdValue) {
        return `pulse:userpage:${Number(userIdValue || 0)}`;
    }

    function profileOrderStorageKey(userIdValue) {
        return `pulse:profile-order:${Number(userIdValue || 0)}`;
    }

    function currentUserpageBody(profile = state.profile.payload, user = state.profile.user) {
        const own = Number(state.session?.user?.id) === Number(user?.id);
        const local = own ? readLocalJson(userpageStorageKey(user?.id)) : null;
        return String(local?.body ?? user?.profile_text ?? profile?.profile_text ?? "");
    }

    function renderProfileUserpage(profile, user, own) {
        const local = own ? readLocalJson(userpageStorageKey(user.id)) : null;
        const body = String(local?.body ?? user.profile_text ?? profile.profile_text ?? "");
        const html = local ? "" : String(user.profile_html ?? profile.profile_html ?? "");
        renderSafeUserpage($("#profile-userpage"), body, html);
        $("#userpage-text").value = body;
        renderSafeUserpage($("#userpage-preview"), body);
    }

    async function readBackOwnProfile(user) {
        const id = Number(user?.id || 0);
        if (!id) return null;
        try {
            const snapshot = await api(`/users/${id}?${new URLSearchParams({ mode: state.mode, _: String(Date.now()) })}`);
            return snapshot?.user || snapshot || null;
        } catch {
            return null;
        }
    }

    async function persistUserpage(body, requestedUser = null) {
        const user = requestedUser || (state.route === "settings" ? state.settingsUser : state.profile.user);
        if (!user) return false;
        if (body.length > USERPAGE_MAX_LENGTH) throw new Error(`Страница игрока не может быть длиннее ${formatNumber(USERPAGE_MAX_LENGTH)} символов`);
        try {
            const result = await api("/me/userpage", { method: "PUT", data: { body } });
            removeLocalValue(userpageStorageKey(user.id));
            const payload = result?.user || result || {};
            user.profile_text = payload.profile_text ?? payload.body ?? body;
            if (payload.profile_html !== undefined) user.profile_html = payload.profile_html;
            return true;
        } catch (error) {
            if (error.status >= 500) {
                const saved = await readBackOwnProfile(user);
                if (saved && String(saved.profile_text || "") === body) {
                    removeLocalValue(userpageStorageKey(user.id));
                    user.profile_text = saved.profile_text;
                    user.profile_html = saved.profile_html || "";
                    return true;
                }
            }
            if (![0, 404, 405, 501].includes(error.status)) throw error;
            if (!writeLocalJson(userpageStorageKey(user.id), { body, saved_at: new Date().toISOString() })) throw error;
            toast("Страница сохранена на этом устройстве — API профиля пока недоступен");
            user.profile_text = body;
            user.profile_html = "";
            return false;
        }
    }

    function openUserpageEditor() {
        const body = currentUserpageBody();
        $("#userpage-text").value = body;
        renderSafeUserpage($("#userpage-preview"), body);
        $("#profile-userpage").hidden = true;
        $("#userpage-editor").hidden = false;
        $("#userpage-text").focus();
    }

    function closeUserpageEditor() {
        $("#profile-userpage").hidden = false;
        $("#userpage-editor").hidden = true;
    }

    async function saveInlineUserpage(event) {
        event.preventDefault();
        const button = event.currentTarget.querySelector('[type="submit"]');
        const body = $("#userpage-text").value.trim();
        setButtonBusy(button, true, "сохраняем…");
        try {
            await persistUserpage(body);
            renderSafeUserpage($("#profile-userpage"), body, state.profile.user?.profile_html || "");
            $("#settings-userpage").value = body;
            closeUserpageEditor();
            toast("Страница игрока сохранена");
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    function normalizedRankHistory(source, currentRank, activityScores) {
        const rows = Array.isArray(source) ? source : getItems(source, "items", "history");
        const points = rows.map((item) => ({
            date: new Date(item.date || item.created_at || item.timestamp),
            rank: Number(item.rank || item.global_rank || item.position || 0),
            pp: item.pp === undefined ? null : Number(item.pp),
        })).filter((item) => item.rank > 0 && !Number.isNaN(item.date.getTime()));
        if (!points.length) {
            activityScores.forEach((score) => {
                const rank = Number(score.global_rank || score.rank_after || score.position_after || 0);
                const date = new Date(score.ended_at || score.created_at);
                if (rank > 0 && !Number.isNaN(date.getTime())) points.push({ date, rank, pp: Number(score.pp || 0) });
            });
        }
        points.sort((a, b) => a.date - b.date);
        if (!points.length && currentRank) points.push({ date: new Date(), rank: Number(currentRank), pp: null });
        const daily = new Map();
        points.forEach((point) => daily.set(`${point.date.getFullYear()}-${point.date.getMonth()}-${point.date.getDate()}`, point));
        return [...daily.values()];
    }

    function showRankChartPoint(index) {
        const points = state.rankChart.points;
        if (!points.length) return;
        const bounded = Math.max(0, Math.min(points.length - 1, index));
        state.rankChart.activeIndex = bounded;
        const point = points[bounded];
        const guide = $("#profile-chart-guide");
        const marker = $("#profile-chart-point");
        guide.hidden = false;
        marker.hidden = false;
        guide.setAttribute("x1", point.x);
        guide.setAttribute("x2", point.x);
        marker.setAttribute("cx", point.x);
        marker.setAttribute("cy", point.y);
        const tooltip = $("#profile-chart-tooltip");
        tooltip.replaceChildren(
            make("strong", "", `#${formatNumber(point.rank)}`),
            make("span", "", formatShortDate(point.date)),
            ...(point.pp === null ? [] : [make("small", "", `${formatNumber(point.pp)} pp`)]),
        );
        tooltip.style.left = `${(point.x / 540) * 100}%`;
        tooltip.style.top = `${Math.max(2, (point.y / 95) * 100 - 4)}%`;
        tooltip.classList.toggle("is-start", bounded === 0);
        tooltip.classList.toggle("is-end", points.length > 1 && bounded === points.length - 1);
        tooltip.hidden = false;
    }

    function hideRankChartPoint() {
        $("#profile-chart-guide").hidden = true;
        $("#profile-chart-point").hidden = true;
        $("#profile-chart-tooltip").hidden = true;
    }

    function renderRankHistory(source, currentRank, activityScores) {
        const raw = normalizedRankHistory(source, currentRank, activityScores);
        const empty = $("#profile-chart-empty");
        empty.hidden = raw.length > 0;
        if (!raw.length) {
            state.rankChart.points = [];
            $("#profile-chart-line").setAttribute("d", "");
            $("#profile-chart-area").setAttribute("d", "");
            hideRankChartPoint();
            return;
        }
        const ranks = raw.map((item) => item.rank);
        const min = Math.min(...ranks);
        const max = Math.max(...ranks);
        const spread = Math.max(1, max - min);
        const firstTimestamp = raw[0].date.getTime();
        const timestampSpan = Math.max(1, raw.at(-1).date.getTime() - firstTimestamp);
        const points = raw.map((item) => ({
            ...item,
            x: raw.length === 1 ? 34 : 12 + ((item.date.getTime() - firstTimestamp) / timestampSpan) * 516,
            y: min === max ? 48 : 10 + ((item.rank - min) / spread) * 69,
        }));
        state.rankChart.points = points;
        state.rankChart.activeIndex = points.length - 1;
        const line = `M0 92 ${points.map((point) => `L${point.x.toFixed(2)} ${point.y.toFixed(2)}`).join(" ")}`;
        $("#profile-chart-line").setAttribute("d", line);
        $("#profile-chart-area").setAttribute("d", `${line} L${points.at(-1).x.toFixed(2)} 92 L0 92 Z`);
        $("#profile-rank-chart").setAttribute("aria-label", `История рейтинга: от #${formatNumber(points[0].rank)} до #${formatNumber(points.at(-1).rank)}`);
        hideRankChartPoint();
    }

    function monthDate(item) {
        if (item.year && item.month) return new Date(Number(item.year), Number(item.month) - 1, 1);
        const raw = item.date || item.start_date;
        if (raw instanceof Date) return new Date(raw.getFullYear(), raw.getMonth(), 1);
        const match = String(raw || "").match(/^(\d{4})-(\d{1,2})/);
        if (match) return new Date(Number(match[1]), Number(match[2]) - 1, 1);
        if (raw) {
            const date = new Date(raw);
            if (!Number.isNaN(date.getTime())) return new Date(date.getFullYear(), date.getMonth(), 1);
        }
        return new Date(NaN);
    }

    function fallbackMonthlyPlaycounts(scores) {
        const counts = new Map();
        scores.forEach((score) => {
            const date = new Date(score.ended_at || score.created_at);
            if (Number.isNaN(date.getTime())) return;
            const key = `${date.getFullYear()}-${date.getMonth()}`;
            const current = counts.get(key) || { date: new Date(date.getFullYear(), date.getMonth(), 1), count: 0 };
            current.count += 1;
            counts.set(key, current);
        });
        return [...counts.values()];
    }

    function normalizedMonthlyPlaycounts(profile, scores) {
        let items = getItems(profile, "monthly_playcounts", "monthlyPlaycounts").map((item) => ({
            date: monthDate(item),
            count: Number(item.count ?? item.play_count ?? 0),
        }));
        items = items.filter((item) => !Number.isNaN(item.date.getTime()) && Number.isFinite(item.count));
        if (!items.length) items = fallbackMonthlyPlaycounts(scores);
        if (!items.length) return [];

        const totals = new Map();
        items.forEach((item) => {
            const date = new Date(item.date.getFullYear(), item.date.getMonth(), 1);
            const key = `${date.getFullYear()}-${date.getMonth()}`;
            const previous = totals.get(key);
            totals.set(key, { date, count: Math.max(0, Number(previous?.count || 0) + Number(item.count || 0)) });
        });
        const available = [...totals.values()].sort((a, b) => a.date - b.date);
        const first = available[0].date;
        const last = available.at(-1).date;
        const filled = [];
        for (const cursor = new Date(first); cursor <= last; cursor.setMonth(cursor.getMonth() + 1)) {
            const date = new Date(cursor.getFullYear(), cursor.getMonth(), 1);
            const key = `${date.getFullYear()}-${date.getMonth()}`;
            filled.push(totals.get(key) || { date, count: 0 });
        }
        return filled;
    }

    function showPlayHistoryPoint(index) {
        const points = state.playHistoryChart.points;
        if (!points.length) return;
        const bounded = Math.max(0, Math.min(points.length - 1, index));
        state.playHistoryChart.activeIndex = bounded;
        const point = points[bounded];
        const guide = $("#profile-play-history-guide");
        const marker = $("#profile-play-history-point");
        guide.hidden = false;
        marker.hidden = false;
        guide.setAttribute("x1", point.x);
        guide.setAttribute("x2", point.x);
        marker.setAttribute("cx", point.x);
        marker.setAttribute("cy", point.y);
        const month = monthYearFormat.format(point.date).replace(/\s*г\.$/u, "");
        const tooltip = $("#profile-play-history-tooltip");
        tooltip.textContent = `${month}: ${formatNumber(point.count)} игр`;
        tooltip.style.left = `${(point.x / 900) * 100}%`;
        tooltip.style.top = `${Math.max(54, point.y)}px`;
        tooltip.classList.toggle("is-start", bounded === 0);
        tooltip.classList.toggle("is-end", points.length > 1 && bounded === points.length - 1);
        tooltip.hidden = false;
    }

    function hidePlayHistoryPoint() {
        $("#profile-play-history-guide").hidden = true;
        $("#profile-play-history-point").hidden = true;
        $("#profile-play-history-tooltip").hidden = true;
    }

    function renderPlayHistory(profile, scores) {
        const items = normalizedMonthlyPlaycounts(profile, scores);
        const empty = $("#profile-play-history-empty");
        const line = $("#profile-play-history-line");
        const area = $("#profile-play-history-area");
        const labels = $("#profile-play-history-labels");
        empty.hidden = items.length > 0;
        labels.replaceChildren();
        if (!items.length) {
            state.playHistoryChart.points = [];
            line.setAttribute("d", "");
            area.setAttribute("d", "");
            hidePlayHistoryPoint();
            return;
        }

        const max = Math.max(1, ...items.map((item) => item.count));
        const points = items.map((item, index) => ({
            ...item,
            x: items.length === 1 ? 36 : 12 + (index / (items.length - 1)) * 876,
            y: 174 - (item.count / max) * 150,
        }));
        state.playHistoryChart.points = points;
        state.playHistoryChart.activeIndex = points.length - 1;
        const path = `M0 174 ${points.map((point) => `L${point.x.toFixed(2)} ${point.y.toFixed(2)}`).join(" ")}`;
        line.setAttribute("d", path);
        area.setAttribute("d", `${path} L${points.at(-1).x.toFixed(2)} 174 L0 174 Z`);

        const labelIndexes = new Set([0, points.length - 1]);
        const labelCount = Math.min(7, points.length);
        for (let index = 0; index < labelCount; index += 1) {
            labelIndexes.add(Math.round((index / Math.max(1, labelCount - 1)) * (points.length - 1)));
        }
        labels.replaceChildren(...[...labelIndexes].sort((a, b) => a - b).map((index) => {
            const point = points[index];
            const label = make("span", "", shortMonthYearFormat.format(point.date).replace(/\s*г\.$/u, ""));
            label.style.left = `${(point.x / 900) * 100}%`;
            if (index === 0) label.classList.add("is-first");
            if (points.length > 1 && index === points.length - 1) label.classList.add("is-last");
            return label;
        }));
        const total = points.reduce((sum, point) => sum + point.count, 0);
        $("#profile-play-history").setAttribute("aria-label", `История игр за ${formatNumber(points.length)} мес.: ${formatNumber(total)} игр`);
        hidePlayHistoryPoint();
    }

    function fallbackMostPlayed(scores) {
        const grouped = new Map();
        scores.forEach((score) => {
            const map = scoreBeatmap(score);
            const key = map.beatmapsetId || map.title;
            const item = grouped.get(key) || {
                play_count: 0,
                beatmap: { id: map.id, beatmapset_id: map.beatmapsetId, version: map.version },
                beatmapset: { id: map.beatmapsetId, title: map.title, artist: map.artist, creator: map.creator, cover_url: map.cover },
            };
            item.play_count += 1;
            grouped.set(key, item);
        });
        return [...grouped.values()].sort((a, b) => b.play_count - a.play_count).slice(0, 6);
    }

    function renderMostPlayed(profile, scores) {
        let items = getItems(profile, "most_played", "mostPlayed");
        if (!items.length) items = fallbackMostPlayed(scores);
        items = items.slice(0, 6);
        const max = Math.max(1, ...items.map((item) => Number(item.play_count || item.count || 0)));
        const root = $("#profile-most-played");
        root.replaceChildren(...items.map((item) => {
            const beatmap = item.beatmap || {};
            const beatmapset = item.beatmapset || beatmap.beatmapset || {};
            const id = Number(beatmapset.id || beatmap.beatmapset_id || 0);
            const count = Number(item.play_count || item.count || 0);
            const row = make("button", "most-played-item");
            row.type = "button";
            const art = make("span", "most-played-art");
            const cover = safeAssetUrl(beatmapset.cover_url || beatmapset.covers?.list || beatmapset.covers?.card);
            if (cover) {
                const image = make("img");
                image.src = cover;
                image.alt = "";
                image.loading = "lazy";
                image.addEventListener("error", () => image.remove(), { once: true });
                art.append(image);
            }
            const copy = make("span", "most-played-copy");
            copy.append(make("strong", "", beatmapset.title || "Неизвестная карта"), make("small", "", `${beatmapset.artist || "Неизвестный артист"} · ${beatmap.version || ""}`));
            const countRoot = make("span", "most-played-count", formatNumber(count));
            const bar = make("i");
            bar.style.width = `${Math.max(6, (count / max) * 100)}%`;
            row.append(art, copy, countRoot, bar);
            if (id) {
                state.beatmapCache.set(id, { ...beatmapset, id, beatmaps: [beatmap] });
                row.addEventListener("click", () => openBeatmap(id));
            } else row.disabled = true;
            return row;
        }));
        $("#profile-most-played-empty").hidden = items.length > 0;
    }

    function renderProfileActivity(profile, scores) {
        renderPlayHistory(profile, scores);
        renderMostPlayed(profile, scores);
    }

    const profileBlocks = ["scores", "about", "medals", "historical"];

    function normalizeProfileOrder(order) {
        const aliases = {
            me: "about",
            recent_activity: "scores",
            top_ranks: "scores",
            medals: "medals",
            historical: "historical",
            scores: "scores",
            about: "about",
        };
        const normalized = [];
        (Array.isArray(order) ? order : []).forEach((item) => {
            const value = aliases[String(item)];
            if (value && !normalized.includes(value)) normalized.push(value);
        });
        profileBlocks.forEach((value) => { if (!normalized.includes(value)) normalized.push(value); });
        return normalized;
    }

    function arrangeProfileBlocks(order) {
        const root = $("#profile-blocks");
        normalizeProfileOrder(order).forEach((name) => {
            const block = $(`[data-profile-block="${name}"]`, root);
            if (block) root.append(block);
        });
    }

    function applyProfileOrder(serverOrder) {
        const user = state.profile.user;
        const own = Number(state.session?.user?.id) === Number(user?.id);
        const local = own ? readLocalJson(profileOrderStorageKey(user?.id)) : null;
        arrangeProfileBlocks(local?.order || serverOrder);
    }

    function currentProfileOrder() {
        return $$("[data-profile-block]", $("#profile-blocks")).map((block) => block.dataset.profileBlock);
    }

    let profileOrderQueue = Promise.resolve();

    function persistProfileOrder() {
        const user = state.profile.user;
        if (!user || Number(state.session?.user?.id) !== Number(user.id)) return Promise.resolve();
        const userIdValue = Number(user.id);
        const order = currentProfileOrder();
        const operation = profileOrderQueue.then(async () => {
            if (Number(state.session?.user?.id) !== userIdValue) return;
            try {
                await api("/me/profile-layout", { method: "PATCH", data: { order } });
                removeLocalValue(profileOrderStorageKey(userIdValue));
                if (Number(state.profile.user?.id) === userIdValue && state.profile.payload) {
                    state.profile.payload.profile_order = order;
                }
            } catch (error) {
                if (![0, 404, 405, 422, 501].includes(error.status)) {
                    toast(error.message, "error");
                    return;
                }
                writeLocalJson(profileOrderStorageKey(userIdValue), { order });
                toast("Порядок блоков сохранён на этом устройстве");
            }
        });
        profileOrderQueue = operation.catch(() => {});
        return operation;
    }

    function moveProfileBlock(block, direction) {
        if (!block || !block.classList.contains("is-owned")) return;
        const sibling = direction < 0 ? block.previousElementSibling : block.nextElementSibling;
        if (!sibling) return;
        if (direction < 0) block.parentElement.insertBefore(block, sibling);
        else block.parentElement.insertBefore(sibling, block);
        persistProfileOrder();
    }

    function profileMetaIcon(kind) {
        const paths = {
            discord: [
                "M5 6.5h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-7l-4.5 3v-3H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2Z",
                "M8 12h.01M12 12h.01M16 12h.01",
            ],
            website: [
                "m9.5 14.5-1.5 1.5a3 3 0 1 1-4.25-4.25l3-3A3 3 0 0 1 11 8",
                "m14.5 9.5 1.5-1.5a3 3 0 1 1 4.25 4.25l-3 3A3 3 0 0 1 13 16",
                "m8.5 15.5 7-7",
            ],
            occupation: [
                "M5 8h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2Z",
                "M9 8V6a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M3 12h18M10 12v2h4v-2",
            ],
        };
        const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.setAttribute("viewBox", "0 0 24 24");
        svg.setAttribute("aria-hidden", "true");
        paths[kind].forEach((definition) => {
            const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
            path.setAttribute("d", definition);
            svg.append(path);
        });
        return svg;
    }

    function makeProfileMetaIcon(kind, label, href = null, onClick = null) {
        const item = make(onClick ? "button" : href ? "a" : "span", `profile-meta-icon is-${kind}`);
        item.dataset.tooltip = label;
        item.setAttribute("aria-label", label);
        if (onClick) {
            item.type = "button";
            item.addEventListener("click", onClick);
        } else if (href) {
            item.href = href;
            item.target = "_blank";
            item.rel = "noopener noreferrer";
        } else {
            item.setAttribute("role", "img");
            item.tabIndex = 0;
        }
        item.append(profileMetaIcon(kind));
        return item;
    }

    function renderProfileMetadata(user) {
        const root = $("#profile-meta");
        const summary = make("div", "profile-meta-summary");
        summary.append(make("span", "", `ID #${user.server_id || user.id}`));
        if (user.join_date) summary.append(make("span", "", `с нами с ${formatDate(user.join_date)}`));
        if (user.is_online) summary.append(make("span", "profile-online", "● сейчас онлайн"));
        const contacts = make("div", "profile-meta-icons");
        const discord = String(user.discord || "").trim();
        if (discord) {
            const copyDiscord = async () => {
                try {
                    await copyTextToClipboard(discord);
                    toast("Discord скопирован");
                } catch {
                    toast("Не удалось скопировать Discord", "error");
                }
            };
            contacts.append(makeProfileMetaIcon("discord", `Discord: ${discord}`, null, copyDiscord));
        }
        if (user.website) {
            const safeUrl = safeAssetUrl(user.website);
            if (safeUrl) contacts.append(makeProfileMetaIcon("website", user.website, safeUrl));
        }
        const occupation = String(user.occupation || "").trim();
        if (occupation) contacts.append(makeProfileMetaIcon("occupation", occupation));
        root.replaceChildren(summary, ...(contacts.childElementCount ? [contacts] : []));
    }

    function renderProfileScores(data) {
        const items = getItems(data, "items", "scores");
        const pagination = getPages(data, state.profile.page);
        state.profile.page = pagination.page;
        state.profile.pages = pagination.pages;
        const root = $("#profile-scores");
        const firstPosition = (pagination.page - 1) * Number(data?.page_size || 20);
        root.replaceChildren(...items.map((score, index) => createScoreRow(score, firstPosition + index + 1)));
        $("#profile-scores-empty").hidden = items.length > 0;
        const total = Number(data?.total ?? items.length);
        $("#profile-score-count").textContent = formatNumber(total);
        $("#profile-score-list-title").textContent = state.profile.scoreType === "best" ? "Лучшие" : "Недавние";
        $("#scores-page").textContent = `${pagination.page} / ${pagination.pages}`;
        $("#scores-prev").disabled = pagination.page <= 1;
        $("#scores-next").disabled = pagination.page >= pagination.pages;
        $("#scores-pagination").hidden = state.profile.scoreType === "best" || pagination.pages <= 1;
        $("#scores-more").hidden = state.profile.scoreType !== "best"
            || state.profile.scoreLimit >= Math.min(200, total)
            || items.length >= 200;
    }

    function renderPinnedScores(data) {
        const items = getItems(data, "items", "scores");
        const group = $("#profile-pinned-group");
        const root = $("#profile-pinned-scores");
        group.hidden = !state.profile.pinningSupported || items.length === 0;
        $("#profile-pinned-count").textContent = formatNumber(items.length);
        root.replaceChildren(...items.map((score, index) => createScoreRow(score, index + 1, { pinned: true })));
    }

    function replayUrlForScore(score) {
        const provided = safeAssetUrl(score.replay_url);
        if (provided) return provided;
        const scoreId = Number(score.id || 0);
        return score.has_replay && scoreId ? new URL(`/api/v2/scores/${scoreId}/download`, location.origin).href : null;
    }

    function closeScoreActionMenu() {
        const root = $("#score-action-menu");
        if (!root) return;
        $$('.score-menu[aria-expanded="true"]').forEach((button) => button.setAttribute("aria-expanded", "false"));
        root.hidden = true;
        root.replaceChildren();
    }

    function showScoreDetails(score) {
        closeScoreActionMenu();
        const dialog = $("#score-detail-dialog");
        dialog.classList.toggle("is-negative-pp", Boolean(score.negative_pp));
        const map = scoreBeatmap(score);
        const grade = scoreGrade(score);
        const gradeRoot = $("#score-detail-grade");
        gradeRoot.className = `score-grade ${gradeClass(grade)}`.trim();
        gradeRoot.textContent = grade;
        $("#score-detail-title").textContent = `${map.title} от ${map.artist}`;
        $("#score-detail-difficulty").textContent = map.version || "Без названия";
        const player = $("#score-detail-player");
        const owner = score.user;
        player.hidden = !owner;
        if (owner) {
            renderAvatar(player.querySelector(".avatar"), owner);
            renderUserName(player.querySelector(".player-name"), owner);
            player.href = `#profile/${userId(owner)}`;
            player.onclick = () => dialog.close();
        }
        const statistics = score.statistics || {};
        const facts = [
            ["Очки", formatNumber(score.total_score || score.score)],
            ["Точность", formatAccuracy(score.accuracy)],
            ["Комбо", `${formatNumber(score.max_combo)}x`],
            ["PP", `${formatNumber(score.pp)} pp`],
            ["Моды", createScoreModDetails(score)],
            ["Попадания", `${formatNumber(statistics.great)} / ${formatNumber(statistics.ok)} / ${formatNumber(statistics.meh)} / ${formatNumber(statistics.miss)}`],
            ["Сыграно", formatDate(score.ended_at || score.created_at)],
        ];
        $("#score-detail-facts").replaceChildren(...facts.map(([label, value]) => {
            const row = make("div");
            const result = make("dd");
            if (value instanceof Node) result.append(value);
            else result.textContent = String(value);
            row.append(make("dt", "", label), result);
            return row;
        }));
        const mapButton = $("#score-detail-map");
        mapButton.dataset.beatmapsetId = String(map.beatmapsetId || "");
        mapButton.dataset.beatmapId = String(map.id || "");
        mapButton.disabled = !map.beatmapsetId;
        const replay = $("#score-detail-replay");
        const replayUrl = replayUrlForScore(score);
        replay.hidden = !replayUrl;
        if (replayUrl) {
            replay.href = replayUrl;
            replay.download = `score-${Number(score.id || 0)}.osr`;
        } else replay.removeAttribute("href");
        if (!dialog.open) dialog.showModal();
    }

    async function setScorePinned(score, shouldPin) {
        const scoreId = Number(score.id || 0);
        if (!scoreId || !state.profile.pinningSupported) return;
        closeScoreActionMenu();
        try {
            await api(`/me/score-pins/${scoreId}`, { method: shouldPin ? "PUT" : "DELETE" });
            toast(shouldPin ? "Рекорд закреплён" : "Рекорд откреплён");
        } catch (error) {
            if ([404, 405, 501].includes(error.status)) state.profile.pinningSupported = false;
            toast(error.message, "error");
            return;
        }
        try { await loadProfile(); } catch { toast("Изменение сохранено — обновите профиль, чтобы увидеть его"); }
    }

    const pinnedReorderQueues = new Map();

    function renumberPinnedScoreRows() {
        [...$("#profile-pinned-scores").children].forEach((row, index) => {
            const position = index + 1;
            const label = $(".score-position", row);
            if (label) label.textContent = `#${position}`;
            const handle = $(".pinned-score-handle", row);
            if (handle && !handle.disabled) {
                handle.setAttribute("aria-label", `Изменить позицию закреплённого результата ${position}`);
            }
        });
    }

    function reconcilePinnedScoreOrder(orderedScoreIds) {
        if (!Array.isArray(orderedScoreIds)) return;
        const root = $("#profile-pinned-scores");
        const rows = new Map(
            [...root.children].map((row) => [Number(row.dataset.scoreId || 0), row]),
        );
        const seen = new Set();
        orderedScoreIds.forEach((value) => {
            const scoreId = Number(value || 0);
            const row = rows.get(scoreId);
            if (!scoreId || !row || seen.has(scoreId)) return;
            seen.add(scoreId);
            root.append(row);
        });
        rows.forEach((row, scoreId) => {
            if (!seen.has(scoreId)) root.append(row);
        });
        renumberPinnedScoreRows();
    }

    function persistPinnedScorePosition(row) {
        const scoreId = Number(row?.dataset.scoreId || 0);
        if (!scoreId) return Promise.resolve();
        const next = row.nextElementSibling;
        const previous = row.previousElementSibling;
        const data = next
            ? { before_score_id: Number(next.dataset.scoreId) }
            : previous
                ? { after_score_id: Number(previous.dataset.scoreId) }
                : null;
        if (!data) return Promise.resolve();

        const profileUserId = Number(state.profile.user?.id || 0);
        const mode = state.mode;
        const queueKey = `${profileUserId}:${mode}`;
        const queue = pinnedReorderQueues.get(queueKey) || { tail: Promise.resolve(), revision: 0, epoch: 0 };
        pinnedReorderQueues.set(queueKey, queue);
        const revision = ++queue.revision;
        const epoch = queue.epoch;
        const isCurrentProfile = () => Number(state.profile.user?.id || 0) === profileUserId && state.mode === mode;

        const operation = queue.tail.then(async () => {
            if (epoch !== queue.epoch) return;
            try {
                const result = await api(`/me/score-pins/${scoreId}/reorder`, { method: "PATCH", data });
                if (epoch === queue.epoch && revision === queue.revision && isCurrentProfile()) {
                    reconcilePinnedScoreOrder(result?.ordered_score_ids);
                }
            } catch (error) {
                if (epoch !== queue.epoch) return;
                queue.epoch += 1;
                toast(error.message, "error");
                if (!isCurrentProfile()) return;
                try {
                    await loadProfile();
                } catch {
                    toast("Не удалось обновить порядок закреплённых рекордов", "error");
                }
            }
        });
        queue.tail = operation.catch(() => {});
        return operation;
    }

    function movePinnedScore(row, direction) {
        if (!row?.draggable) return;
        const sibling = direction < 0 ? row.previousElementSibling : row.nextElementSibling;
        if (!sibling) return;
        if (direction < 0) row.parentElement.insertBefore(row, sibling);
        else row.parentElement.insertBefore(sibling, row);
        renumberPinnedScoreRows();
        busy(persistPinnedScorePosition(row));
        row.querySelector(".pinned-score-handle")?.focus();
    }

    function requestScoreDeletion(score) {
        if (!hasSitePermission("score_delete") || !Number(score?.id || 0)) return;
        closeScoreActionMenu();
        state.pendingScoreDelete = score;
        const map = scoreBeatmap(score);
        const player = score.user ? ` игрока ${userName(score.user)}` : "";
        $("#score-delete-description").textContent = `Результат #${score.id}${player} на карте «${map.title}» будет удалён с сервера. Статистика игрока пересчитается.`;
        const dialog = $("#score-delete-dialog");
        if (!dialog.open) dialog.showModal();
    }

    async function confirmScoreDeletion() {
        const score = state.pendingScoreDelete;
        if (!score || !hasSitePermission("score_delete")) return;
        const button = $("#score-delete-confirm");
        setButtonBusy(button, true, "Удаляем…");
        try {
            await api(`/scores/${Number(score.id)}`, { method: "DELETE" });
            $("#score-delete-dialog").close();
            state.pendingScoreDelete = null;
            toast(`Результат #${Number(score.id)} удалён`);
            if (state.route === "beatmap") await loadBeatmapScores();
            else if (state.route === "profile") await loadProfile();
        } catch (error) {
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    function openScoreActionMenu(anchor, score, options = {}) {
        closeScoreActionMenu();
        const root = $("#score-action-menu");
        root.replaceChildren();
        anchor.setAttribute("aria-expanded", "true");
        const pinned = Boolean(options.pinned);
        const own = Number(state.session?.user?.id) === Number(state.profile.user?.id);
        if (options.allowPin !== false && own && state.profile.pinningSupported) {
            const pin = make("button", "", pinned ? "Открепить" : "Закрепить");
            pin.type = "button";
            pin.setAttribute("role", "menuitem");
            pin.addEventListener("click", () => busy(setScorePinned(score, !pinned)));
            root.append(pin);
        }
        const details = make("button", "", "Подробнее");
        details.type = "button";
        details.setAttribute("role", "menuitem");
        details.addEventListener("click", () => showScoreDetails(score));
        root.append(details);
        const replayUrl = replayUrlForScore(score);
        if (replayUrl) {
            const replay = make("a", "", "Скачать запись");
            replay.href = replayUrl;
            replay.download = `score-${Number(score.id || 0)}.osr`;
            replay.setAttribute("role", "menuitem");
            replay.addEventListener("click", closeScoreActionMenu);
            root.append(replay);
        }
        if (hasSitePermission("score_delete")) {
            const remove = make("button", "is-danger", "Удалить с сервера");
            remove.type = "button";
            remove.setAttribute("role", "menuitem");
            remove.addEventListener("click", () => requestScoreDeletion(score));
            root.append(remove);
        }
        const rect = anchor.getBoundingClientRect();
        root.style.left = `${Math.min(window.innerWidth - 180, Math.max(8, rect.right - 168))}px`;
        root.style.top = `${Math.min(window.innerHeight - root.offsetHeight - 8, rect.bottom + 4)}px`;
        root.hidden = false;
        requestAnimationFrame(() => {
            root.style.top = `${Math.min(window.innerHeight - root.offsetHeight - 8, rect.bottom + 4)}px`;
            root.querySelector("button, a")?.focus();
        });
    }

    function achievementTooltip(item) {
        const detail = item.description ? ` — ${item.description}` : "";
        const date = item.unlocked && item.achieved_at ? ` · получено ${formatDate(item.achieved_at)}` : " · не получено";
        return `${item.name || `Достижение #${item.id}`}${detail}${date}`;
    }

    function createAchievement(item, recent = false) {
        const medal = make("div", `achievement-item${item.unlocked ? "" : " is-locked"}${recent ? " is-recent" : ""}`);
        medal.dataset.tooltip = achievementTooltip(item);
        medal.setAttribute("role", "img");
        medal.setAttribute("aria-label", achievementTooltip(item));
        const imageUrls = [...new Set([item.image_url_2x, item.image_url].map(safeAssetUrl).filter(Boolean))];
        if (imageUrls.length) {
            const image = make("img");
            let imageIndex = 0;
            image.src = imageUrls[imageIndex];
            image.alt = "";
            image.loading = "lazy";
            image.addEventListener("error", () => {
                imageIndex += 1;
                if (imageUrls[imageIndex]) {
                    image.src = imageUrls[imageIndex];
                    return;
                }
                image.remove();
                medal.append(make("span", "achievement-fallback", "◆"));
            });
            medal.append(image);
        } else {
            medal.append(make("span", "achievement-fallback", "◆"));
        }
        return medal;
    }

    function renderProfileAchievements(data) {
        const catalogVisible = data?.catalog_visible === true;
        const latest = getItems(data, "latest").filter((item) => item.unlocked);
        const groups = getItems(data, "groups")
            .map((group) => ({
                ...group,
                achievements: getItems(group, "achievements").filter((item) => catalogVisible || item.unlocked),
            }))
            .filter((group) => group.achievements.length > 0);
        const unlockedCount = Number(data?.unlocked_count || 0);
        $("#profile-medals").textContent = data?.unavailable ? "—" : formatNumber(unlockedCount);
        const total = Number(data?.total || groups.reduce((sum, group) => sum + getItems(group, "achievements").length, 0));
        $("#achievement-count").textContent = catalogVisible
            ? `${formatNumber(unlockedCount)} / ${formatNumber(total)}`
            : formatNumber(unlockedCount);
        $("#profile-achievements-recent").replaceChildren(...latest.map((item) => createAchievement(item, true)));
        const groupsRoot = $("#profile-achievements-grid");
        groupsRoot.replaceChildren(...groups.map((group) => {
            const section = make("section", "achievement-group");
            const groupCount = catalogVisible
                ? `${formatNumber(group.unlocked_count || 0)} / ${formatNumber(group.total || 0)}`
                : formatNumber(group.unlocked_count || group.achievements.length);
            section.append(make("h4", "", `${group.name || group.key} · ${groupCount}`));
            const grid = make("div", "achievement-grid");
            grid.append(...group.achievements.map((item) => createAchievement(item)));
            section.append(grid);
            return section;
        }));
        $("#profile-achievements-empty").hidden = unlockedCount > 0 || groups.length > 0;
    }

    function scoreModList(score) {
        const mods = Array.isArray(score.mods) ? score.mods : [];
        return mods
            .map((mod) => ({
                acronym: String(typeof mod === "string" ? mod : mod?.acronym || "").toUpperCase(),
                settings: typeof mod === "object" && mod?.settings && typeof mod.settings === "object"
                    ? mod.settings
                    : {},
            }))
            .filter((mod) => mod.acronym && mod.acronym !== "NM");
    }

    function scoreRuleset(score) {
        const ruleset = String(score.ruleset || score.mode || score.beatmap?.mode || state.mode || "osu");
        return { osurx: "osu", osuap: "osu", taikorx: "taiko", fruitsrx: "fruits" }[ruleset] || ruleset;
    }

    function scoreModMetadata(score, acronym) {
        return state.modCatalog?.[scoreRuleset(score)]?.[acronym] || null;
    }

    function modCategoryClass(score, acronym) {
        const type = scoreModMetadata(score, acronym)?.type || MOD_CATEGORY_FALLBACKS[acronym] || "Conversion";
        return `is-${String(type).replace(/([a-z])([A-Z])/g, "$1-$2").toLowerCase()}`;
    }

    function modSettingEntries(score, mod) {
        const metadata = scoreModMetadata(score, mod.acronym);
        const descriptors = new Map((metadata?.settings || []).map((setting) => [setting.name, setting]));
        const names = Object.keys(mod.settings || {}).filter((name) => mod.settings[name] !== undefined);
        if (Object.hasOwn(SPEED_MOD_DEFAULTS, mod.acronym) && !names.includes("speed_change")) names.unshift("speed_change");
        return names.map((name) => {
            const descriptor = descriptors.get(name);
            const explicit = Object.hasOwn(mod.settings || {}, name);
            const fallback = name === "speed_change" ? SPEED_MOD_DEFAULTS[mod.acronym] : descriptor?.default;
            return {
                name,
                label: MOD_SETTING_LABELS[name] || descriptor?.label || name.replaceAll("_", " "),
                type: descriptor?.type || typeof mod.settings[name],
                value: explicit ? mod.settings[name] : fallback,
                defaultValue: descriptor?.default ?? fallback,
                explicit,
            };
        }).filter((setting) => setting.value !== undefined);
    }

    function numberWithComma(value, digits = 2) {
        const rounded = Number(value).toFixed(digits).replace(/0+$/, "").replace(/[.,]$/, "");
        return rounded.replace(".", ",");
    }

    function formatModSettingValue(setting) {
        const value = setting.value;
        if (value === null || value === "") return "авто";
        if (setting.type === "boolean" || typeof value === "boolean") return value ? "да" : "нет";
        if (["speed_change", "initial_rate", "final_rate"].includes(setting.name)) return `${numberWithComma(value)}×`;
        if (setting.name === "minimum_accuracy") return `${numberWithComma(Number(value) * 100)}%`;
        if (setting.name === "follow_delay") return `${numberWithComma(value)} мс`;
        return typeof value === "number" ? numberWithComma(value) : String(value);
    }

    function settingIsCustomized(setting) {
        if (!setting.explicit) return false;
        if (setting.defaultValue === null || setting.defaultValue === undefined) return true;
        if (typeof setting.value === "number" && typeof setting.defaultValue === "number") {
            return Math.abs(setting.value - setting.defaultValue) > 0.000001;
        }
        return setting.value !== setting.defaultValue;
    }

    function createScoreMod(mod, score) {
        const metadata = scoreModMetadata(score, mod.acronym);
        const settings = modSettingEntries(score, mod);
        const customized = settings.some(settingIsCustomized);
        const speed = settings.find((setting) => setting.name === "speed_change");
        const customSpeed = speed && settingIsCustomized(speed);
        const wrapper = make("span", `score-mod-wrap${customized ? " is-customized" : ""}`);
        wrapper.tabIndex = 0;
        const icon = make("span", `score-mod ${modCategoryClass(score, mod.acronym)}${customSpeed ? " has-rate-label" : ""}`);
        icon.append(make("b", "", mod.acronym));
        if (customSpeed) icon.append(make("small", "score-mod-rate", formatModSettingValue(speed)));
        wrapper.append(icon);
        const tooltip = [
            `${metadata?.name || mod.acronym} (${mod.acronym})`,
            ...settings.map((setting) => `${setting.label}: ${formatModSettingValue(setting)}`),
        ].join("\n");
        wrapper.dataset.tooltip = tooltip;
        wrapper.setAttribute("aria-label", tooltip.replaceAll("\n", ". "));
        return wrapper;
    }

    function createScoreModDetails(score) {
        const root = make("div", "score-detail-mod-list");
        const mods = scoreModList(score);
        if (!mods.length) {
            root.textContent = "NM";
            return root;
        }
        mods.forEach((mod) => {
            const metadata = scoreModMetadata(score, mod.acronym);
            const item = make("div", "score-detail-mod-entry");
            const heading = make("div", "score-detail-mod-heading");
            heading.append(createScoreMod(mod, score), make("strong", "", metadata?.name || mod.acronym));
            item.append(heading);
            const settings = modSettingEntries(score, mod);
            if (settings.length) {
                item.append(make(
                    "span",
                    "score-detail-mod-settings",
                    settings.map((setting) => `${setting.label}: ${formatModSettingValue(setting)}`).join(" · "),
                ));
            }
            root.append(item);
        });
        return root;
    }

    function createScorePp(score, compact = false) {
        const pp = make("strong", "score-pp");
        if (statusKey(score.beatmap?.status) === "loved") {
            pp.classList.add("is-loved");
            pp.textContent = mapStatusSymbol("loved");
            pp.title = "Loved — карта без PP";
            pp.setAttribute("role", "img");
            pp.setAttribute("aria-label", "Loved");
        } else if (compact) {
            pp.textContent = `${formatNumber(score.pp)} pp`;
        } else {
            pp.append(document.createTextNode(formatNumber(score.pp)), make("small", "", "pp"));
            if (score.weight !== undefined) {
                pp.title = `Вес: ${numberWithComma(score.weight * 100)}% · Вклад: ${formatNumber(score.weighted_pp)} pp`;
            }
        }
        return pp;
    }

    function createScoreRow(score, position, options = {}) {
        const pinnedGroup = Boolean(options.pinned);
        const pinned = pinnedGroup || Boolean(score.is_pinned || Number(score.pinned_order || 0) > 0);
        const own = Number(state.session?.user?.id) === Number(state.profile.user?.id);
        const row = make("article", `score-item${pinnedGroup ? " pinned-score-item" : ""}${score.negative_pp ? " is-negative-pp" : ""}`);
        row.dataset.scoreId = String(Number(score.id || 0));
        const grade = scoreGrade(score);
        const map = scoreBeatmap(score);
        if (pinnedGroup) {
            const handle = make("button", "pinned-score-handle", "☰");
            handle.type = "button";
            handle.title = own ? "Перетащите или используйте стрелки, чтобы изменить порядок" : "Закреплённый рекорд";
            handle.setAttribute("aria-label", own ? `Изменить позицию закреплённого результата ${position}` : "Закреплённый результат");
            handle.disabled = !own;
            row.append(handle);
            row.draggable = own;
        }
        row.append(make("span", `score-grade ${gradeClass(grade)}`.trim(), grade));

        const copy = make("div", "score-map");
        const importSource = score.import?.source === "official_osu"
            ? "с official osu!"
            : score.import?.source || "из другого сервера";
        const importRanking = score.import?.unverified_override_used
            ? " · рейтинг включён вручную"
            : score.import?.ranking_outcome === "visible_unranked"
                ? " · вне рейтинга"
                : "";
        const provenance = score.import
            ? ` · импорт ${importSource}${score.import.source_username ? ` (${score.import.source_username})` : ""}${importRanking}`
            : "";
        const title = make("strong", "score-map-title");
        const titleContent = document.createDocumentFragment();
        titleContent.append(
            make("span", "score-position", `#${position}`),
            document.createTextNode(` ${map.title} от ${map.artist}`),
        );
        const mapUrl = beatmapSiteUrl(map.beatmapsetId, map.id);
        if (mapUrl) {
            const mapLink = make("a", "score-map-link");
            mapLink.href = mapUrl;
            mapLink.draggable = false;
            mapLink.setAttribute("aria-label", `Открыть ${map.title}, сложность ${map.version || "без названия"}`);
            mapLink.append(titleContent);
            title.append(mapLink);
            row.classList.add("has-map-link");
        } else {
            title.append(titleContent);
        }
        const details = make("span", "score-map-details");
        details.append(
            make("b", "score-difficulty", map.version || "Без названия"),
            make("time", "", formatRelativeDate(score.ended_at || score.created_at)),
        );
        copy.append(title, details);

        const mods = make("div", "score-mods");
        mods.append(...scoreModList(score).map((mod) => createScoreMod(mod, score)));

        const accuracy = make("strong", "score-accuracy", formatAccuracy(score.accuracy).replace(".", ","));
        const combo = make("div", "score-combo");
        const currentCombo = Math.max(0, Number(score.max_combo || 0));
        const mapMaxCombo = Math.max(currentCombo, Number(score.beatmap?.max_combo || map.max_combo || 0));
        const comboLine = make("strong", currentCombo === mapMaxCombo && mapMaxCombo > 0 ? "is-full-combo" : "");
        comboLine.append(
            make("span", "score-current-combo", formatNumber(currentCombo)),
            document.createTextNode("/"),
            make("span", "score-map-combo", `${formatNumber(mapMaxCombo)}x`),
        );
        const statistics = score.statistics || {};
        const hitLine = make("span", "score-hitcounts");
        [
            ["is-great", statistics.great],
            ["is-ok", statistics.ok],
            ["is-meh", statistics.meh],
            ["is-miss", statistics.miss],
        ].forEach(([className, value], index) => {
            if (index) hitLine.append(document.createTextNode(" / "));
            hitLine.append(make("b", className, formatNumber(value)));
        });
        combo.append(comboLine, hitLine);

        const pp = createScorePp(score);
        const menu = make("button", "score-menu", "⋮");
        menu.type = "button";
        menu.title = `Результат #${score.id}`;
        menu.setAttribute("aria-label", `Действия с результатом #${score.id}`);
        menu.setAttribute("aria-haspopup", "menu");
        menu.setAttribute("aria-expanded", "false");
        menu.addEventListener("click", (event) => {
            event.stopPropagation();
            openScoreActionMenu(menu, score, { pinned });
        });

        row.title = `${formatDate(score.ended_at || score.created_at)}${provenance}`;
        row.append(copy, mods, accuracy, combo, pp, menu);
        return row;
    }

    function renderSettingsGate() {
        const loggedIn = Boolean(state.session?.user);
        $("#settings-guest").hidden = loggedIn;
        $("#settings-content").hidden = !loggedIn;
    }

    async function loadSettings() {
        renderSettingsGate();
        if (!state.session?.user) return;
        await refreshNotifications();
        const id = state.session.user.id;
        const profile = await api(`/users/${id}?mode=${encodeURIComponent(state.mode)}`);
        const user = profile.user || profile;
        state.settingsProfile = profile;
        state.settingsUser = user;
        populateSettings(user, profile);
        selectSettingsTab(state.settingsTab);
    }

    function populateCountries(selected) {
        const root = $("#settings-country");
        root.replaceChildren();
        const unknown = make("option", "", "🌐 Не указано (XX)");
        unknown.value = "XX";
        root.append(unknown);
        ISO_CODES.map((code) => ({ code, name: regionNames?.of(code) || code }))
            .sort((a, b) => a.name.localeCompare(b.name, "ru"))
            .forEach(({ code, name }) => {
                const option = make("option", "", `${flagEmoji(code)} ${name} (${code})`);
                option.value = code;
                root.append(option);
            });
        root.value = selected && [...root.options].some((option) => option.value === selected) ? selected : "XX";
    }

    function populateSettings(user, profile = state.settingsProfile) {
        renderAvatarEditor();
        renderUserName($("#settings-username"), user);
        renderUserName($("#account-name"), user);
        populateCountries(user.country_code || "XX");
        $("#settings-interests").value = user.interests || "";
        $("#settings-website").value = user.website || "";
        $("#settings-discord").value = user.discord || "";
        $("#settings-occupation").value = user.occupation || "";
        const local = readLocalJson(userpageStorageKey(user.id));
        const body = String(local?.body ?? user.profile_text ?? profile?.profile_text ?? "");
        $("#settings-userpage").value = body;
        renderSafeUserpage($("#settings-userpage-preview"), body);
        $("#settings-email").value = state.session?.user?.email || user.email || "";
    }

    function avatarStatus(message = "", error = false) {
        $("#avatar-status").textContent = message;
        $("#avatar-status").classList.toggle("is-error", error);
    }

    function renderAvatarEditor() {
        const user = state.settingsUser || state.session?.user;
        const preview = $("#settings-avatar-preview");
        if (state.avatar.preview) {
            const image = make("img");
            image.alt = "";
            image.src = state.avatar.preview;
            preview.replaceChildren(image);
            preview.setAttribute("aria-label", "Предпросмотр нового аватара");
        } else {
            renderAvatar(preview, user);
            preview.setAttribute("aria-label", "Текущий аватар");
        }
        const busy = Boolean(state.avatar.request);
        const hasAvatar = user?.has_custom_avatar ?? Boolean(user?.avatar_url && !user.avatar_url.includes("/soms-default-avatar.png"));
        $("#avatar-form").setAttribute("aria-busy", String(busy));
        $("#avatar-file").disabled = busy;
        $("#avatar-choose").disabled = busy;
        $("#avatar-delete").disabled = busy || !hasAvatar;
        $("#avatar-save").disabled = busy || !state.avatar.file;
        $("#avatar-cancel").disabled = busy;
        $("#avatar-pending-actions").hidden = !state.avatar.file;
        $("#avatar-file-name").textContent = state.avatar.file?.name || "";
        $("#avatar-file-name").hidden = !state.avatar.file;
    }

    function clearAvatarSelection() {
        state.avatar.revision++;
        if (state.avatar.preview) URL.revokeObjectURL(state.avatar.preview);
        state.avatar.preview = null;
        state.avatar.file = null;
        $("#avatar-file").value = "";
    }

    async function selectAvatar() {
        const file = $("#avatar-file").files[0];
        if (!file || state.avatar.request) return;
        clearAvatarSelection();
        renderAvatarEditor();
        avatarStatus();
        if (!file.size || file.size > 5 * 1024 * 1024) {
            avatarStatus(file.size ? "Аватар должен быть не больше 5 МБ." : "Выбран пустой файл.", true);
            return;
        }
        if (!/^image\/(png|jpeg|gif|webp)$/.test(file.type)) {
            avatarStatus("Выберите изображение PNG, JPEG, GIF или WebP.", true);
            return;
        }
        const revision = state.avatar.revision;
        const url = URL.createObjectURL(file);
        const image = new Image();
        image.src = url;
        try {
            await image.decode();
            if (revision !== state.avatar.revision) { URL.revokeObjectURL(url); return; }
            if (image.naturalWidth > 2048 || image.naturalHeight > 2048) throw new Error("Размер изображения не должен превышать 2048 × 2048 пикселей.");
            state.avatar.file = file;
            state.avatar.preview = url;
            renderAvatarEditor();
            avatarStatus("Новый аватар выбран. Нажмите «Сохранить аватар».");
        } catch (error) {
            URL.revokeObjectURL(url);
            if (revision !== state.avatar.revision) return;
            avatarStatus(error.name === "EncodingError" ? "Не удалось прочитать изображение." : error.message, true);
        }
    }

    async function changeAvatar(remove = false) {
        if (!state.session?.user || state.avatar.request || (!remove && !state.avatar.file)) return;
        const id = state.session.user.id;
        const controller = new AbortController();
        state.avatar.request = controller;
        renderAvatarEditor();
        avatarStatus(remove ? "Удаляем аватар…" : "Сохраняем аватар…");
        try {
            const form = new FormData();
            if (!remove) form.append("avatar", state.avatar.file);
            const result = await api("/me/avatar", { method: remove ? "DELETE" : "POST", form: remove ? undefined : form, signal: controller.signal });
            if (controller !== state.avatar.request || state.session?.user?.id !== id) return;
            const changes = { avatar_url: result.url, has_custom_avatar: result.has_custom_avatar ?? !remove };
            Object.assign(state.session.user, changes);
            if (state.settingsUser?.id === id) Object.assign(state.settingsUser, changes);
            if (state.profile.user?.id === id) Object.assign(state.profile.user, changes);
            clearAvatarSelection();
            $$(".avatar").filter((element) => element.dataset.userId === String(id))
                .forEach((element) => renderAvatar(element, state.session.user, true));
            updateAuthUI();
            avatarStatus(remove ? "Аватар удалён." : "Аватар сохранён.");
        } catch (error) {
            if (controller === state.avatar.request && error.name !== "AbortError") avatarStatus(error.message, true);
        } finally {
            if (controller === state.avatar.request) {
                state.avatar.request = null;
                renderAvatarEditor();
            }
        }
    }

    async function saveProfile(event) {
        event.preventDefault();
        const button = $("#profile-save");
        setButtonBusy(button, true, "Сохраняем…");
        const optional = (selector) => $(selector).value.trim() || null;
        const userpageBody = $("#settings-userpage").value.trim();
        const previousUserpage = currentUserpageBody(state.settingsProfile, state.settingsUser);
        const changes = {
            country_code: $("#settings-country").value,
            interests: optional("#settings-interests"),
            website: optional("#settings-website"),
            discord: optional("#settings-discord"),
            occupation: optional("#settings-occupation"),
        };
        try {
            const result = await api("/me/profile", {
                method: "PATCH",
                data: changes,
            });
            const user = result.user || result;
            state.settingsUser = { ...state.settingsUser, ...user };
            state.session.user = { ...state.session.user, ...user };
            if (userpageBody !== previousUserpage) await persistUserpage(userpageBody, state.settingsUser);
            updateAuthUI();
            populateSettings(state.settingsUser, state.settingsProfile);
            toast("Профиль сохранён");
        } catch (error) {
            if (error.status >= 500) {
                const saved = await readBackOwnProfile(state.settingsUser);
                const matches = saved && Object.entries(changes).every(([key, value]) => String(saved[key] || "") === String(value || ""));
                if (matches) {
                    state.settingsUser = { ...state.settingsUser, ...saved };
                    state.session.user = { ...state.session.user, ...saved };
                    try {
                        if (userpageBody !== previousUserpage) await persistUserpage(userpageBody, state.settingsUser);
                    } catch (userpageError) {
                        toast(userpageError.message, "error");
                        return;
                    }
                    updateAuthUI();
                    populateSettings(state.settingsUser, state.settingsProfile);
                    toast("Профиль сохранён");
                    return;
                }
            }
            toast(error.message, "error");
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    function selectSettingsTab(tab) {
        const selected = ["security", "notifications"].includes(tab) ? tab : "profile";
        state.settingsTab = selected;
        $$('[data-settings-tab]').forEach((button) => {
            const active = button.dataset.settingsTab === selected;
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-selected", String(active));
        });
        $$('[data-settings-panel]').forEach((panel) => { panel.hidden = panel.dataset.settingsPanel !== selected; });
    }

    function securityFormStatus(form, message, isError = false) {
        const status = $(".security-form-status", form);
        status.textContent = message || "";
        status.classList.toggle("is-error", isError);
    }

    async function securityRequest(name, data) {
        try {
            return await api(`/me/security/${name}`, { method: "PATCH", data });
        } catch (error) {
            if (error.status !== 405) throw error;
            return api(`/me/security/${name}`, { method: "POST", data });
        }
    }

    async function submitSecurityForm(form, name, data, successMessage) {
        const button = form.querySelector('[type="submit"]');
        securityFormStatus(form, "");
        setButtonBusy(button, true, "сохраняем…");
        try {
            const result = await securityRequest(name, data);
            securityFormStatus(form, successMessage);
            return result;
        } catch (error) {
            if ([0, 404, 405, 501].includes(error.status)) {
                securityFormStatus(form, "Эта настройка пока не поддерживается сервером.", true);
            } else {
                securityFormStatus(form, error.message, true);
            }
            return null;
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function saveEmail(event) {
        event.preventDefault();
        const form = event.currentTarget;
        const email = $("#settings-email").value.trim();
        const currentPassword = $("#settings-email-password").value;
        const result = await submitSecurityForm(form, "email", { email, current_password: currentPassword }, "Адрес электронной почты обновлён.");
        if (result && state.session?.user) {
            state.session.user.email = result.email || email;
            $("#settings-email-password").value = "";
        }
    }

    async function savePassword(event) {
        event.preventDefault();
        const form = event.currentTarget;
        const currentPassword = $("#settings-current-password").value;
        const newPassword = $("#settings-new-password").value;
        if (newPassword !== $("#settings-confirm-password").value) {
            securityFormStatus(form, "Новые пароли не совпадают.", true);
            return;
        }
        const result = await submitSecurityForm(
            form,
            "password",
            { current_password: currentPassword, new_password: newPassword },
            "Пароль изменён.",
        );
        if (result) {
            form.reset();
            if (result.reauthenticate) {
                clearSession();
                updateAuthUI();
                toast("Пароль изменён. Войдите снова.");
                openAuth("login");
            }
        }
    }

    async function saveCodeword(event) {
        event.preventDefault();
        const form = event.currentTarget;
        const codeword = $("#settings-codeword").value;
        const currentPassword = $("#settings-codeword-password").value;
        const result = await submitSecurityForm(form, "codeword", { codeword, current_password: currentPassword }, "Кодовое слово сохранено.");
        if (result) form.reset();
    }

    function selectAuthTab(tab) {
        $$('[data-auth-tab]').forEach((button) => {
            const active = button.dataset.authTab === tab;
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-selected", String(active));
        });
        $("#auth-login-panel").hidden = tab !== "login";
        $("#auth-register-panel").hidden = tab !== "register";
        $(tab === "login" ? "#login-username" : "#register-username").focus();
    }

    function openAuth(tab = "login") {
        const dialog = $("#auth-dialog");
        if (!dialog.open) dialog.showModal();
        selectAuthTab(tab);
    }

    function setFormError(id, message) {
        const root = $(id);
        root.textContent = message || "";
        root.hidden = !message;
    }

    async function login(event) {
        event.preventDefault();
        const button = $("#login-submit");
        setFormError("#login-error", "");
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
            state.session = result;
            state.csrf = result.csrf_token;
            $("#login-form").reset();
            $("#login-totp-field").hidden = true;
            $("#auth-dialog").close();
            updateAuthUI();
            toast(`С возвращением, ${result.user.username}!`);
            await busy(loadRoute(state.route));
        } catch (error) {
            if (error.detail?.totp_required) {
                $("#login-totp-field").hidden = false;
                $("#login-totp").focus();
            }
            setFormError("#login-error", error.message);
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function register(event) {
        event.preventDefault();
        const password = $("#register-password").value;
        if (password !== $("#register-confirm").value) {
            setFormError("#register-error", "Пароли не совпадают");
            return;
        }
        const button = $("#register-submit");
        setFormError("#register-error", "");
        setButtonBusy(button, true, "Создаём…");
        try {
            const result = await api("/register", {
                method: "POST",
                data: {
                    username: $("#register-username").value.trim(),
                    email: $("#register-email").value.trim(),
                    password,
                    country_code: $("#register-country").value,
                },
            });
            $("#register-form").reset();
            if (result?.authenticated && result.user) {
                state.session = result;
                state.csrf = result.csrf_token;
                $("#auth-dialog").close();
                updateAuthUI();
                toast("Аккаунт создан. Добро пожаловать!");
                go("profile");
            } else {
                selectAuthTab("login");
                $("#login-username").value = result?.user?.username || result?.username || "";
                setFormError("#login-error", "Аккаунт создан. Теперь войди.");
            }
        } catch (error) {
            setFormError("#register-error", error.message);
        } finally {
            setButtonBusy(button, false, "");
        }
    }

    async function logout(trigger = null) {
        const button = trigger || $("#logout-button");
        const compactTrigger = button.id === "user-menu-logout";
        if (compactTrigger) {
            button.disabled = true;
            button.setAttribute("aria-busy", "true");
        } else setButtonBusy(button, true, "Выходим…");
        try {
            await api("/session", { method: "DELETE" });
        } catch (error) {
            if (error.status !== 401) toast(error.message, "error");
        } finally {
            clearSession();
            updateAuthUI();
            if (compactTrigger) {
                button.disabled = false;
                button.removeAttribute("aria-busy");
            } else setButtonBusy(button, false, "");
            toast("Ты вышел из аккаунта");
            go("home");
        }
    }

    function bindEvents() {
        window.addEventListener("hashchange", route);
        $("#mobile-menu-button").addEventListener("click", () => {
            const header = $(".site-header");
            const open = header.classList.toggle("is-menu-open");
            $("#mobile-menu-button").setAttribute("aria-expanded", String(open));
        });
        $$('[data-route]').forEach((link) => link.addEventListener("click", () => {
            $(".site-header").classList.remove("is-menu-open");
            $("#mobile-menu-button").setAttribute("aria-expanded", "false");
        }));
        $$('[data-go]').forEach((button) => button.addEventListener("click", () => go(button.dataset.go)));
        $$('[data-open-auth]').forEach((button) => button.addEventListener("click", () => openAuth(button.dataset.openAuth)));
        $("#auth-button").addEventListener("click", () => openAuth("login"));
        $("#global-search-button").addEventListener("click", openGlobalSearch);
        $("#global-search-close").addEventListener("click", closeGlobalSearch);
        $("#global-search-dialog").addEventListener("click", (event) => {
            if (event.target === $("#global-search-dialog")) closeGlobalSearch();
        });
        let globalSearchTimer;
        $("#global-search-input").addEventListener("input", () => {
            window.clearTimeout(globalSearchTimer);
            globalSearchTimer = window.setTimeout(searchGlobalPlayers, 250);
        });
        $("#global-search-input").addEventListener("keydown", (event) => {
            if (event.key === "Enter") {
                event.preventDefault();
                window.clearTimeout(globalSearchTimer);
                searchGlobalPlayers();
            }
        });
        $("#global-map-search-link").addEventListener("click", () => {
            const query = $("#global-search-input").value.trim();
            state.beatmaps.query = query;
            $("#map-search").value = query;
            resetBeatmapPagination();
            closeGlobalSearch();
            go("beatmaps");
        });
        $("#support-soms").addEventListener("click", () => {
            window.open("https://github.com/GooGuTeam/g0v0-server", "_blank", "noopener,noreferrer");
        });
        $("#user-menu-button").addEventListener("click", toggleUserMenu);
        $("#user-menu-profile").addEventListener("click", () => {
            closeUserMenu();
            openProfile(userId(state.session?.user));
        });
        $("#user-menu-settings").addEventListener("click", () => {
            closeUserMenu();
            go("settings");
        });
        $("#user-menu-admin").addEventListener("click", () => {
            closeUserMenu();
            if (hasSitePermission("admin_panel")) window.location.assign("/admin/");
        });
        $("#user-menu-logout").addEventListener("click", (event) => logout(event.currentTarget));
        $(".user-menu-shell").addEventListener("focusout", (event) => {
            if (event.relatedTarget && !event.currentTarget.contains(event.relatedTarget)) closeUserMenu();
        });
        $("#profile-settings-button").addEventListener("click", () => go("settings"));
        $("#profile-avatar-edit").addEventListener("click", () => {
            if (Number(state.profile.user?.id) !== Number(state.session?.user?.id)) return;
            state.settingsTab = "profile";
            go("settings");
        });
        $("#profile-friend-button").addEventListener("click", () => busy(toggleProfileFriendship()));
        $("#beatmap-detail-back").addEventListener("click", () => go("beatmaps"));
        bindBeatmapPageEvents();
        $("#beatmap-rank").addEventListener("click", (event) => busy(moderateBeatmapset("rank", event.currentTarget)));
        $("#beatmap-unrank").addEventListener("click", (event) => busy(moderateBeatmapset("unrank", event.currentTarget)));
        $("#beatmap-love").addEventListener("click", (event) => busy(moderateBeatmapset("love", event.currentTarget)));
        $("#score-delete-confirm").addEventListener("click", () => busy(confirmScoreDeletion()));
        $("#score-delete-dialog").addEventListener("close", () => { state.pendingScoreDelete = null; });
        $("#beatmap-scores-prev").addEventListener("click", () => {
            if (state.beatmapDetail.scorePage <= 1) return;
            state.beatmapDetail.scorePage -= 1;
            busy(loadBeatmapScores()).catch((error) => toast(error.message, "error"));
        });
        $("#beatmap-scores-next").addEventListener("click", () => {
            if (state.beatmapDetail.scorePage >= state.beatmapDetail.scorePages) return;
            state.beatmapDetail.scorePage += 1;
            busy(loadBeatmapScores()).catch((error) => toast(error.message, "error"));
        });
        $$("[data-scroll-to]").forEach((button) => button.addEventListener("click", () => {
            document.getElementById(button.dataset.scrollTo)?.scrollIntoView({ behavior: "smooth", block: "start" });
        }));
        $("#global-mode").addEventListener("change", (event) => setMode(event.target.value));
        $$('[data-ranking-section]').forEach((button) => button.addEventListener("click", () => {
            const section = button.dataset.rankingSection;
            if (!rankingViews[section] || section === state.rankings.section) return;
            state.rankings.section = section;
            state.rankings.page = 1;
            busy(loadRankings()).catch((error) => toast(error.message, "error"));
        }));
        $$('[data-ranking-sort]').forEach((button) => button.addEventListener("click", () => {
            const sort = button.dataset.rankingSort;
            if (!["performance", "score"].includes(sort) || sort === state.rankings.sort) return;
            state.rankings.sort = sort;
            state.rankings.page = 1;
            busy(loadRankings()).catch((error) => toast(error.message, "error"));
        }));
        $$('[data-ranking-scope]').forEach((button) => button.addEventListener("click", () => {
            const scope = button.dataset.rankingScope;
            if (!["all", "friends"].includes(scope) || scope === state.rankings.scope) return;
            if (scope === "friends" && !state.session) {
                toast("Войдите, чтобы открыть рейтинг друзей", "error");
                openAuth("login");
                return;
            }
            state.rankings.scope = scope;
            state.rankings.page = 1;
            busy(loadRankings()).catch((error) => toast(error.message, "error"));
        }));
        $("#ranking-country").addEventListener("change", (event) => {
            state.rankings.country = event.target.value;
            state.rankings.page = 1;
            busy(loadRankings()).catch((error) => toast(error.message, "error"));
        });
        $("#rankings-prev").addEventListener("click", () => { if (state.rankings.page > 1) { state.rankings.page -= 1; busy(loadRankings()).catch((error) => toast(error.message, "error")); } });
        $("#rankings-next").addEventListener("click", () => { if (state.rankings.page < state.rankings.pages) { state.rankings.page += 1; busy(loadRankings()).catch((error) => toast(error.message, "error")); } });

        let mapSearchTimer;
        $("#map-search").addEventListener("input", (event) => {
            window.clearTimeout(mapSearchTimer);
            state.beatmaps.query = event.target.value.trim();
            resetBeatmapPagination();
            mapSearchTimer = window.setTimeout(() => {
                if (state.route === "beatmaps") busy(loadBeatmaps()).catch((error) => toast(error.message, "error"));
            }, 350);
        });
        $$('[data-status]').forEach((button) => button.addEventListener("click", () => {
            state.beatmaps.status = button.dataset.status;
            resetBeatmapPagination();
            $$('[data-status]').forEach((chip) => chip.classList.toggle("is-active", chip === button));
            busy(loadBeatmaps()).catch((error) => toast(error.message, "error"));
        }));
        $("#map-sort").addEventListener("change", (event) => {
            state.beatmaps.sort = event.target.value;
            resetBeatmapPagination();
            busy(loadBeatmaps()).catch((error) => toast(error.message, "error"));
        });
        $("#beatmaps-prev").addEventListener("click", () => busy(navigateBeatmaps(-1)).catch((error) => toast(error.message, "error")));
        $("#beatmaps-next").addEventListener("click", () => busy(navigateBeatmaps(1)).catch((error) => toast(error.message, "error")));

        $$('[data-score-type]').forEach((button) => button.addEventListener("click", () => {
            state.profile.scoreType = button.dataset.scoreType;
            state.profile.page = 1;
            state.profile.scoreLimit = 5;
            $$('[data-score-type]').forEach((chip) => chip.classList.toggle("is-active", chip === button));
            if (state.profile.id) busy(loadProfile()).catch((error) => toast(error.message, "error"));
        }));
        $("#scores-prev").addEventListener("click", () => { if (state.profile.page > 1) { state.profile.page -= 1; busy(loadProfile()).catch((error) => toast(error.message, "error")); } });
        $("#scores-next").addEventListener("click", () => { if (state.profile.page < state.profile.pages) { state.profile.page += 1; busy(loadProfile()).catch((error) => toast(error.message, "error")); } });
        $("#scores-more").addEventListener("click", () => {
            state.profile.scoreLimit = Math.min(200, state.profile.scoreLimit + 50);
            state.profile.page = 1;
            busy(loadProfile()).catch((error) => toast(error.message, "error"));
        });
        $("#first-scores-more").addEventListener("click", async () => {
            const button = $("#first-scores-more");
            const requestId = state.profile.requestId;
            button.disabled = true;
            try {
                const data = await api(`/users/${userId(state.profile.user)}/scores?${new URLSearchParams({ mode: state.mode, type: "first", page: String(state.profile.firstScorePage + 1), page_size: "5" })}`);
                if (requestId !== state.profile.requestId || state.route !== "profile") return;
                state.profile.firstScorePage = data.page;
                const displayed = new Set($$("#profile-first-scores [data-score-id]").map((row) => row.dataset.scoreId));
                $("#profile-first-scores").append(...getItems(data, "items").filter((score) => !displayed.has(String(score.id))).map((score) => createScoreRow(score, 1)));
                $("#profile-first-count").textContent = formatNumber(data.total);
                button.hidden = data.page >= data.pages;
            } catch (error) { toast(error.message, "error"); }
            finally { button.disabled = false; }
        });

        document.addEventListener("pointerdown", (event) => {
            const menu = $("#score-action-menu");
            if (!menu.hidden && !event.target.closest("#score-action-menu, .score-menu")) closeScoreActionMenu();
            if (!$("#user-menu-popover").hidden && !event.target.closest(".user-menu-shell")) closeUserMenu();
        });
        document.addEventListener("keydown", (event) => {
            if (event.key !== "Escape") return;
            closeScoreActionMenu();
            if (!$("#user-menu-popover").hidden) {
                event.preventDefault();
                closeUserMenu(true);
            }
        });
        window.addEventListener("resize", closeScoreActionMenu);
        window.addEventListener("scroll", closeScoreActionMenu, { passive: true });
        $("#score-detail-map").addEventListener("click", (event) => {
            const beatmapsetId = Number(event.currentTarget.dataset.beatmapsetId || 0);
            const beatmapId = Number(event.currentTarget.dataset.beatmapId || 0);
            $("#score-detail-dialog").close();
            openBeatmap(beatmapsetId, beatmapId);
        });

        const pinnedRoot = $("#profile-pinned-scores");
        let draggedPinnedScore = null;
        let draggedPinnedStartOrder = [];
        let pinnedDropHandled = false;
        pinnedRoot.addEventListener("dragstart", (event) => {
            const row = event.target.closest(".pinned-score-item");
            if (!row?.draggable) {
                event.preventDefault();
                return;
            }
            draggedPinnedScore = row;
            draggedPinnedStartOrder = [...pinnedRoot.children].map((item) => Number(item.dataset.scoreId || 0));
            pinnedDropHandled = false;
            row.classList.add("is-dragging");
            event.dataTransfer.effectAllowed = "move";
            event.dataTransfer.setData("text/plain", row.dataset.scoreId);
        });
        pinnedRoot.addEventListener("dragover", (event) => {
            if (!draggedPinnedScore) return;
            event.preventDefault();
            const target = event.target.closest(".pinned-score-item");
            if (!target || target === draggedPinnedScore) return;
            const before = event.clientY < target.getBoundingClientRect().top + target.offsetHeight / 2;
            pinnedRoot.insertBefore(draggedPinnedScore, before ? target : target.nextElementSibling);
        });
        pinnedRoot.addEventListener("drop", (event) => {
            if (!draggedPinnedScore) return;
            event.preventDefault();
            pinnedDropHandled = true;
            renumberPinnedScoreRows();
            busy(persistPinnedScorePosition(draggedPinnedScore));
        });
        pinnedRoot.addEventListener("dragend", () => {
            if (draggedPinnedScore && !pinnedDropHandled) reconcilePinnedScoreOrder(draggedPinnedStartOrder);
            draggedPinnedScore?.classList.remove("is-dragging");
            draggedPinnedScore = null;
            draggedPinnedStartOrder = [];
            pinnedDropHandled = false;
        });
        pinnedRoot.addEventListener("keydown", (event) => {
            const handle = event.target.closest(".pinned-score-handle");
            if (!handle || !["ArrowUp", "ArrowDown"].includes(event.key)) return;
            event.preventDefault();
            movePinnedScore(handle.closest(".pinned-score-item"), event.key === "ArrowUp" ? -1 : 1);
        });

        ["#ranking-somsai-format", "#ranking-somsai-variant"].forEach(selector => $(selector).addEventListener("change", () => { state.rankings.page = 1; busy(loadRankings()).catch((error) => toast(error.message, "error")); }));
        $("#profile-somsai-format").addEventListener("change", (event) => {
            state.profile.somsaiFormat = event.target.value;
            busy(loadProfile()).catch((error) => toast(error.message, "error"));
        });
        $("#profile-somsai-variant").addEventListener("change", (event) => {
            state.profile.somsaiVariant = Number(event.target.value) === 7 ? 7 : 4;
            busy(loadProfile()).catch((error) => toast(error.message, "error"));
        });
        const rankChart = $("#profile-rank-chart");
        rankChart.addEventListener("pointermove", (event) => {
            if (!state.rankChart.points.length) return;
            const rect = rankChart.getBoundingClientRect();
            const ratio = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
            const chartX = ratio * 540;
            const nearestIndex = state.rankChart.points.reduce((best, point, index, points) =>
                Math.abs(point.x - chartX) < Math.abs(points[best].x - chartX) ? index : best, 0);
            showRankChartPoint(nearestIndex);
        });
        rankChart.addEventListener("pointerleave", hideRankChartPoint);
        rankChart.addEventListener("focus", () => showRankChartPoint(state.rankChart.activeIndex));
        rankChart.addEventListener("blur", hideRankChartPoint);
        rankChart.addEventListener("keydown", (event) => {
            if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) || !state.rankChart.points.length) return;
            event.preventDefault();
            if (event.key === "Home") state.rankChart.activeIndex = 0;
            else if (event.key === "End") state.rankChart.activeIndex = state.rankChart.points.length - 1;
            else state.rankChart.activeIndex += event.key === "ArrowLeft" ? -1 : 1;
            showRankChartPoint(state.rankChart.activeIndex);
        });

        const playHistoryChart = $("#profile-play-history");
        playHistoryChart.addEventListener("pointermove", (event) => {
            if (!state.playHistoryChart.points.length) return;
            const rect = playHistoryChart.getBoundingClientRect();
            const ratio = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
            showPlayHistoryPoint(Math.round(ratio * (state.playHistoryChart.points.length - 1)));
        });
        playHistoryChart.addEventListener("pointerleave", hidePlayHistoryPoint);
        playHistoryChart.addEventListener("focus", () => showPlayHistoryPoint(state.playHistoryChart.activeIndex));
        playHistoryChart.addEventListener("blur", hidePlayHistoryPoint);
        playHistoryChart.addEventListener("keydown", (event) => {
            if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) || !state.playHistoryChart.points.length) return;
            event.preventDefault();
            if (event.key === "Home") state.playHistoryChart.activeIndex = 0;
            else if (event.key === "End") state.playHistoryChart.activeIndex = state.playHistoryChart.points.length - 1;
            else state.playHistoryChart.activeIndex += event.key === "ArrowLeft" ? -1 : 1;
            showPlayHistoryPoint(state.playHistoryChart.activeIndex);
        });

        const profileBlocksRoot = $("#profile-blocks");
        let draggedBlock = null;
        let draggedBlockStartOrder = [];
        let profileDropHandled = false;
        profileBlocksRoot.addEventListener("dragstart", (event) => {
            const block = event.target.closest("[data-profile-block]");
            if (!block?.classList.contains("is-owned")) {
                event.preventDefault();
                return;
            }
            draggedBlock = block;
            draggedBlockStartOrder = currentProfileOrder();
            profileDropHandled = false;
            block.classList.add("is-dragging");
            event.dataTransfer.effectAllowed = "move";
            event.dataTransfer.setData("text/plain", block.dataset.profileBlock);
        });
        profileBlocksRoot.addEventListener("dragover", (event) => {
            if (!draggedBlock) return;
            event.preventDefault();
            const target = event.target.closest("[data-profile-block]");
            if (!target || target === draggedBlock) return;
            const before = event.clientY < target.getBoundingClientRect().top + target.offsetHeight / 2;
            profileBlocksRoot.insertBefore(draggedBlock, before ? target : target.nextElementSibling);
        });
        profileBlocksRoot.addEventListener("drop", (event) => {
            if (!draggedBlock) return;
            event.preventDefault();
            profileDropHandled = true;
            persistProfileOrder();
        });
        profileBlocksRoot.addEventListener("dragend", () => {
            if (draggedBlock && !profileDropHandled) arrangeProfileBlocks(draggedBlockStartOrder);
            draggedBlock?.classList.remove("is-dragging");
            draggedBlock = null;
            draggedBlockStartOrder = [];
            profileDropHandled = false;
        });
        profileBlocksRoot.addEventListener("click", (event) => {
            const button = event.target.closest("[data-block-move]");
            if (!button) return;
            moveProfileBlock(button.closest("[data-profile-block]"), button.dataset.blockMove === "up" ? -1 : 1);
        });

        const insertEditorText = (before, after = "", placeholder = "текст") => {
            const textarea = $("#userpage-text");
            const start = textarea.selectionStart;
            const end = textarea.selectionEnd;
            const selected = textarea.value.slice(start, end) || placeholder;
            textarea.setRangeText(`${before}${selected}${after}`, start, end, "end");
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
            textarea.focus();
        };
        $("#userpage-edit").addEventListener("click", openUserpageEditor);
        $("#userpage-cancel").addEventListener("click", closeUserpageEditor);
        $("#userpage-editor").addEventListener("submit", saveInlineUserpage);
        $("#userpage-text").addEventListener("input", (event) => renderSafeUserpage($("#userpage-preview"), event.target.value));
        $$('[data-editor-tag]').forEach((button) => button.addEventListener("click", () => {
            const tag = button.dataset.editorTag;
            insertEditorText(`[${tag}]`, `[/${tag}]`);
        }));
        $("[data-editor-link]").addEventListener("click", () => {
            const url = window.prompt("Адрес ссылки", "https://");
            if (!url) return;
            if (!safeAssetUrl(url)) {
                toast("Разрешены только ссылки http:// и https://", "error");
                return;
            }
            insertEditorText(`[url=${url}]`, "[/url]", "название ссылки");
        });
        $("[data-editor-image]").addEventListener("click", () => {
            const url = window.prompt("Прямая ссылка на изображение", "https://");
            if (!url) return;
            if (!safeAssetUrl(url)) {
                toast("Разрешены только изображения по http:// и https://", "error");
                return;
            }
            insertEditorText("[img]", "[/img]", url);
        });

        $$('[data-auth-tab]').forEach((button) => button.addEventListener("click", () => selectAuthTab(button.dataset.authTab)));
        $("#login-form").addEventListener("submit", login);
        $("#register-form").addEventListener("submit", register);
        $("#profile-settings-form").addEventListener("submit", saveProfile);
        $("#avatar-choose").addEventListener("click", () => $("#avatar-file").click());
        $("#avatar-file").addEventListener("change", selectAvatar);
        $("#avatar-form").addEventListener("submit", (event) => { event.preventDefault(); changeAvatar(); });
        $("#avatar-delete").addEventListener("click", () => changeAvatar(true));
        $("#avatar-cancel").addEventListener("click", () => { clearAvatarSelection(); renderAvatarEditor(); avatarStatus(); });
        $("#settings-userpage").addEventListener("input", (event) => renderSafeUserpage($("#settings-userpage-preview"), event.target.value));
        $$('[data-settings-tab]').forEach((button) => button.addEventListener("click", () => selectSettingsTab(button.dataset.settingsTab)));
        $("#email-form").addEventListener("submit", saveEmail);
        $("#password-form").addEventListener("submit", savePassword);
        $("#codeword-form").addEventListener("submit", saveCodeword);
        $("#logout-button").addEventListener("click", (event) => logout(event.currentTarget));
    }

    async function restoreSession() {
        try {
            const result = await api("/session");
            if (result?.authenticated && result.user) {
                state.session = result;
                state.csrf = result.csrf_token;
            }
        } catch (error) {
            if (error.status === 0) toast(error.message, "error");
        }
        updateAuthUI();
    }

    async function loadModCatalog() {
        try {
            const result = await api("/mods");
            state.modCatalog = result?.rulesets && typeof result.rulesets === "object" ? result.rulesets : {};
        } catch {
            state.modCatalog = {};
        }
    }

    async function start() {
        bindEvents();
        renderModeTabs();
        populateCountries("XX");
        const countryOptions = [...$("#settings-country").options].map((option) => option.cloneNode(true));
        $("#register-country").replaceChildren(...countryOptions);
        $("#register-country").value = "XX";
        await Promise.all([restoreSession(), loadModCatalog()]);
        if (!location.hash) history.replaceState(null, "", `${location.pathname}${location.search}#home`);
        await route();
        const initialSearch = new URLSearchParams(location.search).get("search");
        if (initialSearch !== null) {
            $("#global-search-input").value = initialSearch;
            openGlobalSearch();
            searchGlobalPlayers();
        }
    }

    let notificationItems = [];
    let notificationLoading = false;
    function closeNotifications() {
        $("#notifications-popover").hidden = true;
        $("#notifications-button").setAttribute("aria-expanded", "false");
    }
    async function refreshNotifications(append = false) {
        if (!state.session?.user || notificationLoading) return;
        const owner = state.session.user.id;
        notificationLoading = true;
        try {
            const before = append ? notificationItems.at(-1)?.id : null;
            const result = await api(`/notifications${before ? `?before=${before}` : ""}`);
            if (state.session?.user?.id !== owner) return;
            notificationItems = append ? [...notificationItems, ...result.items] : result.items;
            $("#notifications-count").textContent = result.unread > 99 ? "99+" : String(result.unread);
            $("#notifications-count").hidden = !result.unread;
            $("#notifications-button").setAttribute("aria-label", `Уведомления: ${result.unread} непрочитанных`);
            $("#notifications-read").disabled = !result.unread;
            $("#notifications-more").hidden = result.items.length < 30;
            if (!$("#notification-settings-form").contains(document.activeElement)) {
                $("#notify-rank-lost").checked = result.preferences.rank_lost;
                $("#notify-friend-added").checked = result.preferences.friend_added;
                $("#notify-friend-removed").checked = result.preferences.friend_removed !== false;
            }
            const root = $("#notifications-items");
            root.replaceChildren();
            if (!notificationItems.length) root.append(make("p", "notifications-empty", result.supporter ? "Пока нет уведомлений" : "Уведомления о друзьях и первых местах доступны с supporter."));
            for (const item of notificationItems) {
                const row = make("article", `notification-item${item.is_read ? "" : " is-unread"}`);
                const player = make("a", "", item.data.username);
                player.href = `#profile/${Number(item.data.user_id)}`;
                const action = { friend_added: " добавил вас в друзья.", friend_removed: " удалил вас из друзей.", rank_lost: " забрал ваше первое место на " };
                row.append(player, document.createTextNode(action[item.kind] || ""));
                if (item.kind === "rank_lost") {
                    const map = make("a", "", `${item.data.title} (${item.data.mode})`);
                    map.href = beatmapSiteUrl(item.data.beatmapset_id, item.data.beatmap_id);
                    row.append(map);
                }
                row.append(make("small", "", formatDate(item.created_at)));
                row.addEventListener("click", async (event) => {
                    if (!event.target.closest("a")) return;
                    // Only this notification; reading an older item must not clear newer ones.
                    await api(`/notifications/${item.id}/read`, { method: "PUT" }).catch(() => {});
                    closeNotifications();
                    refreshNotifications().catch(() => {});
                });
                root.append(row);
            }
        } finally { notificationLoading = false; }
    }
    $("#notifications-button").addEventListener("click", () => {
        const opening = $("#notifications-popover").hidden;
        $("#notifications-popover").hidden = !opening;
        $("#notifications-button").setAttribute("aria-expanded", String(opening));
        if (opening) refreshNotifications().catch((error) => toast(error.message, "error"));
    });
    $("#notifications-read").addEventListener("click", async () => {
        if (!notificationItems.length) return;
        try {
            await api(`/notifications/read?through=${notificationItems[0].id}`, { method: "PUT" });
            await refreshNotifications();
        } catch (error) { toast(error.message, "error"); }
    });
    $("#notifications-more").addEventListener("click", () => refreshNotifications(true).catch((error) => toast(error.message, "error")));
    $("#notifications-settings-link").addEventListener("click", () => { state.settingsTab = "notifications"; selectSettingsTab("notifications"); closeNotifications(); });
    $("#notification-settings-form").addEventListener("submit", async (event) => {
        event.preventDefault();
        try {
            await api("/notifications/preferences", { method: "PUT", data: {
                rank_lost: $("#notify-rank-lost").checked, friend_added: $("#notify-friend-added").checked,
                friend_removed: $("#notify-friend-removed").checked,
            } });
            toast("Настройки уведомлений сохранены");
        } catch (error) { toast(error.message, "error"); }
    });
    document.addEventListener("click", (event) => { if (!event.target.closest("#notifications-shell")) closeNotifications(); });
    document.addEventListener("keydown", (event) => { if (event.key === "Escape") closeNotifications(); });
    window.setInterval(async () => {
        if (document.hidden || state.route !== "home") return;
        try {
            const data = await api("/home/online");
            if (state.route !== "home") return;
            $("#hero-online").textContent = formatCompact(data.online);
            renderOnlineHistory(data.online_history);
        } catch { /* Keep the last successful sample during a reconnect. */ }
    }, 30_000);
    window.setInterval(() => { if (!document.hidden) refreshNotifications().catch(() => {}); }, 30_000);
    start();
})();
