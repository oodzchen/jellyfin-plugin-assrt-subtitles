(function () {
    "use strict";

    if (window.__assrtSubtitleEditorEnhanced) {
        return;
    }
    window.__assrtSubtitleEditorEnhanced = true;

    const i18n = {
        "zh-hans": {
            labelCustomQuery: "自定义搜索关键字 (Assrt)",
            placeholderCustomQuery: "留空则使用下方自动搜索词；可输入中/英文片名、季集号（多词用 | 分隔）",
            clearTitle: "清空关键字",
            labelAutoQueries: "自动搜索词（点击可填入修改）：",
            labelLoadingQueries: "正在获取默认搜索词...",
            labelAppendEpisode: "自动补全季集号",
            hintTagPrefix: "标签词: "
        },
        "zh-hant": {
            labelCustomQuery: "自訂搜尋關鍵字 (Assrt)",
            placeholderCustomQuery: "留空則使用下方自動搜尋詞；可輸入中/英文片名、季集號（多詞用 | 分隔）",
            clearTitle: "清空關鍵字",
            labelAutoQueries: "自動搜尋詞（點擊可填入修改）：",
            labelLoadingQueries: "正在取得預設搜尋詞...",
            labelAppendEpisode: "自動補全季集號",
            hintTagPrefix: "標籤詞: "
        },
        "en": {
            labelCustomQuery: "Custom Search Keyword (Assrt)",
            placeholderCustomQuery: "Leave empty to use auto queries below; enter title or S01E01 (separate multiple with |)",
            clearTitle: "Clear keyword",
            labelAutoQueries: "Auto queries (click to edit):",
            labelLoadingQueries: "Loading default queries...",
            labelAppendEpisode: "Auto-append season/episode",
            hintTagPrefix: "Tag: "
        }
    };

    function getStrings() {
        const htmlEl = document.documentElement;
        const rawCulture = (
            htmlEl.getAttribute("data-culture") ||
            htmlEl.getAttribute("lang") ||
            navigator.language ||
            "en"
        ).toLowerCase();

        if (rawCulture.startsWith("zh")) {
            if (rawCulture.includes("tw") || rawCulture.includes("hk") || rawCulture.includes("mo") || rawCulture.includes("hant")) {
                return i18n["zh-hant"];
            }
            return i18n["zh-hans"];
        }
        return i18n.en;
    }

    let lastDetectedItemId = null;

    function extractItemIdFromUrl(urlStr) {
        if (!urlStr || typeof urlStr !== "string") {
            return null;
        }
        // Match /Items/{guid} or /Users/{uid}/Items/{guid}
        const m = urlStr.match(/\/Items\/([0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12})(?:$|[/?])/i);
        if (m && m[1]) {
            return m[1];
        }
        return null;
    }

    function getActiveDialogState() {
        const dialog = document.querySelector(".subtitleEditorDialog");
        if (!dialog) {
            return null;
        }
        const input = dialog.querySelector("#assrtCustomQueryInput");
        if (!input) {
            return null;
        }
        const appendCheckbox = dialog.querySelector("#assrtAppendEpisodeCheckbox");
        return {
            dialog: dialog,
            query: (input.value || "").trim(),
            appendEpisode: appendCheckbox ? Boolean(appendCheckbox.checked) : true
        };
    }

    function decorateSubtitleSearchUrl(urlStr) {
        if (!urlStr || typeof urlStr !== "string") {
            return urlStr;
        }

        // Track item ID whenever getItem is called
        if (!urlStr.includes("/RemoteSearch/Subtitles/")) {
            const maybeId = extractItemIdFromUrl(urlStr);
            if (maybeId) {
                lastDetectedItemId = maybeId;
                const openDialog = document.querySelector(".subtitleEditorDialog");
                if (openDialog && !openDialog.getAttribute("data-assrt-itemid")) {
                    openDialog.setAttribute("data-assrt-itemid", maybeId);
                    loadPreviewQueriesForDialog(openDialog, maybeId);
                }
            }
            return urlStr;
        }

        // Intercept /Items/{itemId}/RemoteSearch/Subtitles/{language}
        const subSearchMatch = urlStr.match(/\/Items\/([^/]+)\/RemoteSearch\/Subtitles\/([^/?]+)/i);
        if (!subSearchMatch) {
            return urlStr;
        }

        const itemIdFromSearch = subSearchMatch[1];
        if (itemIdFromSearch) {
            lastDetectedItemId = itemIdFromSearch;
            const openDialog = document.querySelector(".subtitleEditorDialog");
            if (openDialog && !openDialog.getAttribute("data-assrt-itemid")) {
                openDialog.setAttribute("data-assrt-itemid", itemIdFromSearch);
                loadPreviewQueriesForDialog(openDialog, itemIdFromSearch);
            }
        }

        const state = getActiveDialogState();
        if (!state) {
            return urlStr;
        }

        const sep = urlStr.includes("?") ? "&" : "?";
        return (
            urlStr +
            sep +
            "assrtQuery=" +
            encodeURIComponent(state.query) +
            "&assrtAppendEpisode=" +
            (state.appendEpisode ? "1" : "0")
        );
    }

    // Wrap window.fetch
    if (typeof window.fetch === "function") {
        const originalFetch = window.fetch;
        window.fetch = function (input, init) {
            try {
                if (typeof input === "string") {
                    input = decorateSubtitleSearchUrl(input);
                } else if (input && typeof input.url === "string") {
                    const newUrl = decorateSubtitleSearchUrl(input.url);
                    if (newUrl !== input.url) {
                        input = new Request(newUrl, input);
                    }
                }
            } catch (err) {
                console.debug("[AssrtSubtitles] fetch hook error:", err);
            }
            return originalFetch.call(this, input, init);
        };
    }

    // Wrap XMLHttpRequest.prototype.open as fallback
    if (typeof XMLHttpRequest !== "undefined" && XMLHttpRequest.prototype) {
        const originalOpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function (method, url) {
            try {
                if (typeof url === "string") {
                    arguments[1] = decorateSubtitleSearchUrl(url);
                }
            } catch (err) {
                console.debug("[AssrtSubtitles] XHR hook error:", err);
            }
            return originalOpen.apply(this, arguments);
        };
    }

    function injectStyles() {
        if (document.getElementById("assrtCustomSearchStyles")) {
            return;
        }
        const style = document.createElement("style");
        style.id = "assrtCustomSearchStyles";
        style.textContent = `
            .assrt-custom-search-container {
                margin-top: 0.85em;
                margin-bottom: 0.35em;
                padding: 0.75em 0.9em;
                border-radius: 6px;
                background: rgba(255, 255, 255, 0.04);
                border: 1px solid rgba(255, 255, 255, 0.09);
            }
            .assrt-input-header {
                display: flex;
                align-items: center;
                justify-content: space-between;
                flex-wrap: wrap;
                gap: 0.5em;
                margin-bottom: 0.4em;
            }
            .assrt-input-label {
                font-size: 0.88em;
                font-weight: 500;
                color: rgba(255, 255, 255, 0.85);
            }
            .assrt-episode-toggle {
                display: inline-flex;
                align-items: center;
                gap: 0.35em;
                font-size: 0.82em;
                color: rgba(255, 255, 255, 0.75);
                cursor: pointer;
                user-select: none;
            }
            .assrt-episode-toggle input {
                cursor: pointer;
                margin: 0;
            }
            .assrt-input-wrapper {
                position: relative;
                display: flex;
                align-items: center;
            }
            .assrt-custom-input {
                width: 100%;
                padding-right: 2.2em !important;
                box-sizing: border-box;
            }
            .assrt-clear-btn {
                position: absolute;
                right: 0.35em;
                background: transparent;
                border: none;
                color: rgba(255, 255, 255, 0.55);
                cursor: pointer;
                font-size: 1.2em;
                line-height: 1;
                padding: 0.2em 0.4em;
                border-radius: 4px;
                display: none;
            }
            .assrt-clear-btn:hover {
                color: #fff;
                background: rgba(255, 255, 255, 0.1);
            }
            .assrt-chips-row {
                margin-top: 0.55em;
                display: flex;
                align-items: center;
                flex-wrap: wrap;
                gap: 0.4em;
                font-size: 0.8em;
                color: rgba(255, 255, 255, 0.6);
            }
            .assrt-query-chip {
                display: inline-flex;
                align-items: center;
                padding: 0.18em 0.6em;
                border-radius: 999px;
                background: rgba(0, 164, 220, 0.16);
                border: 1px solid rgba(0, 164, 220, 0.38);
                color: rgba(255, 255, 255, 0.9);
                cursor: pointer;
                font-size: 0.95em;
                transition: background 0.15s ease, border-color 0.15s ease;
                max-width: 100%;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .assrt-query-chip:hover {
                background: rgba(0, 164, 220, 0.32);
                border-color: rgba(0, 164, 220, 0.7);
            }
            .assrt-query-chip.assrt-tag-chip {
                background: rgba(76, 175, 80, 0.18);
                border-color: rgba(76, 175, 80, 0.45);
            }
        `;
        document.head.appendChild(style);
    }

    function resolveCurrentItemId(dialog) {
        const attrId = dialog.getAttribute("data-assrt-itemid");
        if (attrId) {
            return attrId;
        }
        if (lastDetectedItemId) {
            return lastDetectedItemId;
        }
        const hash = window.location.hash || "";
        const m = hash.match(/[?&]id=([0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12})/i);
        return m ? m[1] : null;
    }

    function buildPreviewApiUrl(itemId) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === "function") {
            return window.ApiClient.getUrl("AssrtSubtitles/PreviewQueries", { itemId: itemId });
        }
        return "AssrtSubtitles/PreviewQueries?itemId=" + encodeURIComponent(itemId);
    }

    function loadPreviewQueriesForDialog(dialog, itemId) {
        if (!dialog || !itemId) {
            return;
        }
        const chipsContainer = dialog.querySelector("#assrtPreviewChipsContainer");
        const episodeToggleLabel = dialog.querySelector("#assrtEpisodeToggleLabel");
        const episodeSuffixSpan = dialog.querySelector("#assrtEpisodeSuffixText");
        if (!chipsContainer) {
            return;
        }

        const strings = getStrings();
        const url = buildPreviewApiUrl(itemId);

        fetch(url, { headers: { Accept: "application/json" } })
            .then(function (res) {
                return res.ok ? res.json() : null;
            })
            .then(function (data) {
                if (!data) {
                    return;
                }
                const box = dialog.querySelector("#assrtCustomSearchContainer");
                if (data.enableCustomUi === false && box) {
                    box.style.display = "none";
                    return;
                }

                if (data.isEpisode && episodeToggleLabel) {
                    episodeToggleLabel.style.display = "inline-flex";
                    if (episodeSuffixSpan && data.seasonNumber != null && data.episodeNumber != null) {
                        const s = String(data.seasonNumber).padStart(2, "0");
                        const e = String(data.episodeNumber).padStart(2, "0");
                        episodeSuffixSpan.textContent = " (S" + s + "E" + e + ")";
                    } else if (episodeSuffixSpan && data.episodeNumber != null) {
                        const e = String(data.episodeNumber).padStart(2, "0");
                        episodeSuffixSpan.textContent = " (E" + e + ")";
                    }
                } else if (episodeToggleLabel) {
                    episodeToggleLabel.style.display = "none";
                }

                chipsContainer.innerHTML = "";
                const queries = Array.isArray(data.queries) ? data.queries : [];
                if (queries.length === 0) {
                    return;
                }

                const labelSpan = document.createElement("span");
                labelSpan.textContent = strings.labelAutoQueries;
                chipsContainer.appendChild(labelSpan);

                const input = dialog.querySelector("#assrtCustomQueryInput");
                const clearBtn = dialog.querySelector("#assrtClearQueryBtn");

                queries.slice(0, 5).forEach(function (q) {
                    const chip = document.createElement("button");
                    chip.type = "button";
                    chip.className = "assrt-query-chip";
                    chip.textContent = q;
                    chip.title = q;
                    chip.addEventListener("click", function () {
                        if (input) {
                            input.value = q;
                            if (clearBtn) {
                                clearBtn.style.display = "block";
                            }
                            input.focus();
                        }
                    });
                    chipsContainer.appendChild(chip);
                });
            })
            .catch(function (err) {
                console.debug("[AssrtSubtitles] Failed to fetch preview queries:", err);
            });
    }

    function enhanceSubtitleDialog(dialog) {
        if (!dialog || dialog.querySelector("#assrtCustomSearchContainer")) {
            return;
        }

        const form = dialog.querySelector(".subtitleSearchForm");
        if (!form) {
            return;
        }

        injectStyles();
        const strings = getStrings();

        const container = document.createElement("div");
        container.id = "assrtCustomSearchContainer";
        container.className = "assrt-custom-search-container";

        container.innerHTML = `
            <div class="assrt-input-header">
                <label class="assrt-input-label" for="assrtCustomQueryInput">${strings.labelCustomQuery}</label>
                <label id="assrtEpisodeToggleLabel" class="assrt-episode-toggle" style="display:none;">
                    <input id="assrtAppendEpisodeCheckbox" type="checkbox" checked />
                    <span>${strings.labelAppendEpisode}<span id="assrtEpisodeSuffixText"></span></span>
                </label>
            </div>
            <div class="assrt-input-wrapper">
                <input is="emby-input" id="assrtCustomQueryInput" type="text" class="emby-input assrt-custom-input" placeholder="${strings.placeholderCustomQuery}" autocomplete="off" />
                <button type="button" id="assrtClearQueryBtn" class="assrt-clear-btn" title="${strings.clearTitle}">&times;</button>
            </div>
            <div id="assrtPreviewChipsContainer" class="assrt-chips-row"></div>
        `;

        const firstFlexRow = form.querySelector(".flex.align-items-center");
        if (firstFlexRow && firstFlexRow.nextSibling) {
            form.insertBefore(container, firstFlexRow.nextSibling);
        } else {
            form.appendChild(container);
        }

        const input = container.querySelector("#assrtCustomQueryInput");
        const clearBtn = container.querySelector("#assrtClearQueryBtn");

        if (input && clearBtn) {
            input.addEventListener("input", function () {
                clearBtn.style.display = input.value.length > 0 ? "block" : "none";
            });

            input.addEventListener("keydown", function (e) {
                if (e.key === "Enter") {
                    e.preventDefault();
                    e.stopPropagation();
                    form.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
                }
            });

            clearBtn.addEventListener("click", function () {
                input.value = "";
                clearBtn.style.display = "none";
                input.focus();
            });
        }

        const itemId = resolveCurrentItemId(dialog);
        if (itemId) {
            dialog.setAttribute("data-assrt-itemid", itemId);
            loadPreviewQueriesForDialog(dialog, itemId);
        }
    }

    const observer = new MutationObserver(function () {
        const dialog = document.querySelector(".subtitleEditorDialog");
        if (dialog && !dialog.querySelector("#assrtCustomSearchContainer")) {
            enhanceSubtitleDialog(dialog);
        }
    });

    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    } else {
        window.addEventListener("DOMContentLoaded", function () {
            observer.observe(document.body, { childList: true, subtree: true });
        });
    }
})();
